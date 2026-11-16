using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Sas;

namespace AzureDocumentIntelligenceLab.Commands;

/// <summary>Run custom/composed models, classify, batch, and measure accuracy against ground truth.</summary>
public static class AnalysisCommands
{
    /// <summary>analyze &lt;modelId&gt; &lt;file-or-url&gt;: works for custom and composed models.</summary>
    public static async Task<int> AnalyzeAsync(Clients c, Args a)
    {
        var modelId = a.Pos(0, "modelId");
        var op = await c.Analysis.AnalyzeDocumentAsync(WaitUntil.Completed, Parse.AnalyzeOptions(modelId, a.Pos(1, "file-or-url")));
        var r = op.Value;
        Console.WriteLine($"model={r.ModelId} api={r.ApiVersion} pages={r.Pages.Count} documents={r.Documents.Count}");
        foreach (var d in r.Documents) Printer.PrintDocument(d, a.Dbl("min-confidence", 0.8));
        if (a.Opt("json") is { } path) await File.WriteAllTextAsync(path, op.GetRawResponse().Content.ToString());
        return 0;
    }

    /// <summary>classify &lt;classifierId&gt; &lt;file&gt; [--split auto|none|perPage]</summary>
    public static async Task<int> ClassifyAsync(Clients c, Args a)
    {
        var classifierId = a.Pos(0, "classifierId");
        var src = a.Pos(1, "file-or-url");
        var options = Parse.IsUrl(src)
            ? new ClassifyDocumentOptions(classifierId, new Uri(src))
            : new ClassifyDocumentOptions(classifierId, BinaryData.FromBytes(File.ReadAllBytes(src)));
        options.Split = Parse.Split(a.Opt("split"));

        var op = await c.Analysis.ClassifyDocumentAsync(WaitUntil.Completed, options);
        Console.WriteLine($"{op.Value.Documents.Count} document(s) found in {op.Value.Pages.Count} page(s):");
        foreach (var d in op.Value.Documents)
        {
            var pages = string.Join(",", d.BoundingRegions.Select(r => r.PageNumber).Distinct());
            Console.WriteLine($"  {d.DocumentType,-20} confidence={d.Confidence:P1}  pages=[{pages}]");
        }
        return 0;
    }

    /// <summary>
    /// Batch analysis: the service reads every document under a blob prefix and writes one result
    /// JSON per document to a results container - no per-file polling, built for thousands of files.
    /// </summary>
    public static async Task<int> BatchAsync(Clients c, Args a)
    {
        var modelId = a.Pos(0, "modelId");
        var prefix = a.Opt("prefix") ?? "batch-input/";
        var resultPrefix = a.Opt("result-prefix") ?? $"batch-results/{modelId}/";
        await BlobHelper.EnsureContainerAsync(c.Blobs, c.Settings.ResultsContainer);

        var input = await BlobHelper.GetContainerSasAsync(c.Blobs, c.Settings.TrainingContainer,
            BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List, TimeSpan.FromHours(6));
        var output = await BlobHelper.GetContainerSasAsync(c.Blobs, c.Settings.ResultsContainer,
            BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Write | BlobContainerSasPermissions.List
            | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Add, TimeSpan.FromHours(6));

        var options = new AnalyzeBatchDocumentsOptions(modelId, new BlobContentSource(input) { Prefix = prefix }, output)
        {
            ResultPrefix = resultPrefix,
            OverwriteExisting = true,
        };
        Console.WriteLine($"Batch analyzing {c.Settings.TrainingContainer}/{prefix} with '{modelId}' ...");
        var op = await c.Analysis.AnalyzeBatchDocumentsAsync(WaitUntil.Completed, options);
        var r = op.Value;
        Console.WriteLine($"succeeded={r.SucceededCount} failed={r.FailedCount} skipped={r.SkippedCount}");
        foreach (var d in r.Details)
            Console.WriteLine($"  {d.Status,-10} {Path.GetFileName(d.SourceUri?.AbsolutePath)}  ->  {Path.GetFileName(d.ResultUri?.AbsolutePath)} {d.Error?.Message}");
        Console.WriteLine($"Results are in {c.Settings.ResultsContainer}/{resultPrefix}");
        return 0;
    }

    private sealed record Row(string Doc, string DocType, string Field, string Expected, string? Actual, float? Confidence)
    {
        public bool Correct => Actual is not null && Norm(Actual) == Norm(Expected);
        private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    /// <summary>
    /// evaluate &lt;modelId&gt; [--split test|training] [--type docType]
    /// Runs the model on held-out documents and compares every field with ground-truth.json:
    /// per-field accuracy, confidence calibration, and the coverage/precision trade-off you get
    /// by auto-accepting only fields above a confidence threshold.
    /// </summary>
    public static async Task<int> EvaluateAsync(Clients c, Args a)
    {
        var modelId = a.Pos(0, "modelId");
        var split = a.Opt("split") ?? "test";
        var type = a.Opt("type");                  // required for single-layout models; omit for composed models
        var truthPath = Path.GetFullPath(a.Opt("truth") ?? Path.Combine(DefaultPaths.SampleData, "ground-truth.json"));
        var root = Path.GetDirectoryName(truthPath)!;

        var cases = new List<(string Rel, string DocType, Dictionary<string, string> Fields)>();
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath)))
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                if (!entry.Name.StartsWith(split + "/", StringComparison.Ordinal)) continue;
                if (!entry.Value.TryGetProperty("fields", out var fields)) continue;      // skips mixed packets
                var docType = entry.Value.GetProperty("docType").GetString()!;
                if (type is not null && docType != type) continue;
                cases.Add((entry.Name, docType, fields.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString() ?? "")));
            }
        if (cases.Count == 0) throw new InvalidOperationException("No matching documents in ground truth (check --split/--type).");

        var model = (await c.Admin.GetModelAsync(modelId)).Value;
        var composed = model.ClassifierId is not null;
        Console.WriteLine($"Evaluating '{modelId}' on {cases.Count} documents ({(composed ? "composed model, routing is checked too" : "single model")})");

        var rows = new List<Row>();
        int routed = 0, routedOk = 0;
        foreach (var (rel, docType, expected) in cases)
        {
            var op = await c.Analysis.AnalyzeDocumentAsync(WaitUntil.Completed,
                Parse.AnalyzeOptions(modelId, Path.Combine(root, rel)));
            var doc = op.Value.Documents.OrderByDescending(d => d.Confidence).FirstOrDefault();

            if (composed)
            {
                routed++;
                if (doc is not null && doc.DocumentType.EndsWith(docType, StringComparison.OrdinalIgnoreCase)) routedOk++;
            }
            foreach (var (field, want) in expected)
            {
                DocumentField? got = null;
                doc?.Fields.TryGetValue(field, out got);
                rows.Add(new Row(rel, docType, field, want, got is null ? null : got.ValueString ?? got.Content, got?.Confidence));
            }
        }

        if (composed) Console.WriteLine($"\nRouting accuracy: {routedOk}/{routed}");

        Console.WriteLine($"\n{"Field",-18} {"n",3} {"correct",8} {"accuracy",9} {"missing",8} {"avg conf (ok)",14} {"avg conf (wrong)",17}");
        foreach (var g in rows.GroupBy(r => r.Field).OrderBy(grp => grp.Key))
        {
            var ok = g.Where(r => r.Correct).ToList();
            var bad = g.Where(r => !r.Correct && r.Actual is not null).ToList();
            Console.WriteLine($"{g.Key,-18} {g.Count(),3} {ok.Count,8} {(double)ok.Count / g.Count(),9:P0} {g.Count(r => r.Actual is null),8} " +
                              $"{Avg(ok),14} {Avg(bad),17}");
        }
        Console.WriteLine($"\nOverall field accuracy: {rows.Count(r => r.Correct)}/{rows.Count} = {(double)rows.Count(r => r.Correct) / rows.Count:P1}");
        Console.WriteLine($"Documents with every field right: {rows.GroupBy(r => r.Doc).Count(g => g.All(r => r.Correct))}/{cases.Count}");

        Console.WriteLine($"\nAuto-accept policy (fields with confidence >= threshold):");
        Console.WriteLine($"{"threshold",10} {"coverage",9} {"precision",10}");
        foreach (var t in new[] { 0.0, 0.5, 0.7, 0.8, 0.9, 0.95 })
        {
            var accepted = rows.Where(r => r.Confidence is { } cf && cf >= t).ToList();
            var precision = accepted.Count == 0 ? double.NaN : (double)accepted.Count(r => r.Correct) / accepted.Count;
            Console.WriteLine($"{t,10:0.00} {(double)accepted.Count / rows.Count,9:P0} {precision,10:P1}");
        }

        Directory.CreateDirectory("out");
        var csvPath = Path.Combine("out", $"evaluation-{modelId}.csv");
        var sb = new StringBuilder("document,docType,field,expected,actual,confidence,correct\n");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Csv(r.Doc), Csv(r.DocType), Csv(r.Field), Csv(r.Expected), Csv(r.Actual ?? ""),
                r.Confidence?.ToString("0.000", CultureInfo.InvariantCulture) ?? "", r.Correct ? "1" : "0"));
        await File.WriteAllTextAsync(csvPath, sb.ToString());
        Console.WriteLine($"\nPer-field results: {csvPath}");
        return 0;

        static string Avg(List<Row> list)
        {
            var confs = list.Where(r => r.Confidence is not null).Select(r => (double)r.Confidence!.Value).ToList();
            return confs.Count == 0 ? "-" : confs.Average().ToString("0.00", CultureInfo.InvariantCulture);
        }
        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
