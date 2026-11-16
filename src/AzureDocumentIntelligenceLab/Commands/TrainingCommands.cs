using System.Globalization;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Sas;

namespace AzureDocumentIntelligenceLab.Commands;

/// <summary>Custom extraction models, classifiers, composed models and cross-resource copy.</summary>
public static class TrainingCommands
{
    private const BlobContainerSasPermissions ReadList = BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List;

    /// <summary>
    /// Build a custom extraction model from a labeled folder in the training container
    /// (fields.json + *.pdf + *.pdf.labels.json + *.pdf.ocr.json).
    /// template = fixed layouts, fast/cheap to train; neural = variable layouts, trains longer.
    /// </summary>
    public static async Task<int> TrainAsync(Clients c, Args a)
    {
        var set = a.Pos(0, "training-set-folder").Trim('/');
        var modelId = a.Pos(1, "modelId");
        var mode = (a.Opt("mode") ?? "template").ToLowerInvariant() switch
        {
            "template" => DocumentBuildMode.Template,
            "neural" => DocumentBuildMode.Neural,
            _ => throw new ArgumentException("--mode must be template or neural."),
        };

        var sas = await BlobHelper.GetContainerSasAsync(c.Blobs, c.Settings.TrainingContainer, ReadList, TimeSpan.FromHours(6));
        var options = new BuildDocumentModelOptions(modelId, mode, new BlobContentSource(sas) { Prefix = set + "/" })
        {
            Description = $"{set} ({mode}) built {DateTime.UtcNow:u} by azure-document-intelligence-lab",
            AllowOverwrite = a.Flag("overwrite"),
        };
        options.Tags["trainingSet"] = set;
        if (a.Opt("max-hours") is { } hours)                       // neural only: cap billable training time
            options.MaxTrainingHours = float.Parse(hours, CultureInfo.InvariantCulture);

        Console.WriteLine($"Building {mode} model '{modelId}' from {c.Settings.TrainingContainer}/{set}/ ...");
        var op = await c.Admin.BuildDocumentModelAsync(a.Flag("no-wait") ? WaitUntil.Started : WaitUntil.Completed, options);
        if (!op.HasCompleted)
        {
            Console.WriteLine($"Started (operation {op.Id}). Check progress later with: model {modelId}");
            return 0;
        }
        Printer.PrintModel(op.Value);
        return 0;
    }

    /// <summary>
    /// Train a document classifier. Each sub-folder under classifier/ (created by `upload --classifier`)
    /// becomes one class. --base extends an existing classifier (incremental training).
    /// </summary>
    public static async Task<int> TrainClassifierAsync(Clients c, Args a)
    {
        var classifierId = a.Pos(0, "classifierId");
        var prefix = a.Opt("prefix") ?? "classifier/";
        var container = c.Blobs.GetBlobContainerClient(c.Settings.TrainingContainer);
        var sas = await BlobHelper.GetContainerSasAsync(c.Blobs, c.Settings.TrainingContainer, ReadList, TimeSpan.FromHours(6));

        var docTypes = new Dictionary<string, ClassifierDocumentTypeDetails>();
        await foreach (var item in container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix))
        {
            if (!item.IsPrefix) continue;
            var cls = item.Prefix[prefix.Length..].TrimEnd('/');
            docTypes[cls] = new ClassifierDocumentTypeDetails(new BlobContentSource(sas) { Prefix = item.Prefix });
        }
        if (docTypes.Count < 2)
            throw new InvalidOperationException($"Found {docTypes.Count} class folders under '{prefix}'. Run `upload --classifier` first (need >= 2 classes, >= 5 docs each).");

        var options = new BuildClassifierOptions(classifierId, docTypes)
        {
            Description = $"classes: {string.Join(", ", docTypes.Keys)}",
            AllowOverwrite = a.Flag("overwrite"),
            BaseClassifierId = a.Opt("base"),
        };
        Console.WriteLine($"Training classifier '{classifierId}' with classes: {string.Join(", ", docTypes.Keys)}");
        var op = await c.Admin.BuildClassifierAsync(a.Flag("no-wait") ? WaitUntil.Started : WaitUntil.Completed, options);
        if (!op.HasCompleted) { Console.WriteLine($"Started (operation {op.Id})."); return 0; }
        Printer.PrintClassifier(op.Value);
        return 0;
    }

    /// <summary>
    /// v4 composed model = classifier + one extraction model per class.
    /// Usage: compose &lt;composedModelId&gt; &lt;classifierId&gt; class=modelId [class=modelId ...]
    /// </summary>
    public static async Task<int> ComposeAsync(Clients c, Args a)
    {
        var composedId = a.Pos(0, "composedModelId");
        var classifierId = a.Pos(1, "classifierId");
        var map = new Dictionary<string, DocumentTypeDetails>();
        foreach (var pair in a.Positional.Skip(2))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) throw new ArgumentException($"Expected class=modelId, got '{pair}'.");
            var details = new DocumentTypeDetails { ModelId = parts[1] };
            // Only analyze a segment if the classifier is at least this sure about its class.
            if (a.Opt("min-class-confidence") is { } t) details.ConfidenceThreshold = float.Parse(t, CultureInfo.InvariantCulture);
            map[parts[0]] = details;
        }
        if (map.Count == 0) throw new ArgumentException("Provide at least one class=modelId mapping.");

        var options = new ComposeModelOptions(composedId, classifierId, map)
        {
            Description = "composed by azure-document-intelligence-lab",
            Split = Parse.Split(a.Opt("split")),   // auto = split a multi-document PDF into segments
        };
        Console.WriteLine($"Composing '{composedId}' from classifier '{classifierId}' ...");
        var op = await c.Admin.ComposeModelAsync(WaitUntil.Completed, options);
        Printer.PrintModel(op.Value);
        return 0;
    }

    private static async Task PreflightAsync(string role, DocumentIntelligenceAdministrationClient admin, Uri endpoint, string settings)
    {
        try
        {
            var r = (await admin.GetResourceDetailsAsync()).Value;
            Console.WriteLine($"{role}: {endpoint.Host} ok ({r.CustomDocumentModels.Count}/{r.CustomDocumentModels.Limit} custom models)");
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new InvalidOperationException(
                $"The {role} resource ({endpoint.Host}) rejected the credentials (HTTP {ex.Status}). " +
                $"Check that the key belongs to that exact resource ({settings}).");
        }
    }

    /// <summary>Copy a model to another Document Intelligence resource (e.g. dev -> prod, or another region).</summary>
    public static async Task<int> CopyAsync(Clients c, Args a)
    {
        var modelId = a.Pos(0, "modelId");
        var endpoint = new Uri(a.Opt("target-endpoint") ?? throw new ArgumentException("--target-endpoint is required."));
        var key = a.Opt("target-key") ?? throw new ArgumentException("--target-key is required.");
        var targetModelId = a.Opt("target-model-id") ?? modelId;

        // var target = Clients.CreateAdmin(endpoint, key);
        var target = Clients.CreateAdmin(endpoint, key.Trim());
        // Fail early and say WHICH resource rejected its credentials.
        await PreflightAsync("target", target, endpoint, "--target-endpoint / --target-key");
        await PreflightAsync("source", c.Admin, c.Settings.Endpoint, c.Settings.Key is null
            ? "DOCINTEL_KEY is not set, so Entra ID was used; export DOCINTEL_KEY or grant your login the Cognitive Services User role"
            : "DOCINTEL_ENDPOINT / DOCINTEL_KEY");

        // 1) the TARGET resource issues a short-lived copy authorization ...
        var auth = await target.AuthorizeModelCopyAsync(new AuthorizeModelCopyOptions(targetModelId));
        // 2) ... the SOURCE resource pushes the model using it.
        var op = await c.Admin.CopyModelToAsync(WaitUntil.Completed, modelId, auth.Value);
        Console.WriteLine($"Copied '{modelId}' to {endpoint} as '{targetModelId}'.");
        Printer.PrintModel(op.Value);
        return 0;
    }
}
