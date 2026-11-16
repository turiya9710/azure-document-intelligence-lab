namespace AzureDocumentIntelligenceLab.Commands;

/// <summary>Housekeeping: list/inspect/delete models and classifiers, check the custom-model quota.</summary>
public static class AdminCommands
{
    public static async Task<int> ModelsAsync(Clients c, Args a)
    {
        await foreach (var m in c.Admin.GetModelsAsync())
        {
            if (m.ModelId.StartsWith("prebuilt-", StringComparison.Ordinal)) continue;
            var kind = m.BuildMode?.ToString() ?? (m.ClassifierId is null ? "?" : "composed");
            Console.WriteLine($"{m.ModelId,-32} {kind,-9} {m.CreatedOn:yyyy-MM-dd HH:mm}  {m.Description}");
        }
        return 0;
    }

    public static async Task<int> ModelAsync(Clients c, Args a)
    {
        Printer.PrintModel((await c.Admin.GetModelAsync(a.Pos(0, "modelId"))).Value);
        return 0;
    }

    public static async Task<int> ClassifiersAsync(Clients c, Args a)
    {
        await foreach (var k in c.Admin.GetClassifiersAsync())
            Console.WriteLine($"{k.ClassifierId,-32} {k.CreatedOn:yyyy-MM-dd HH:mm}  classes: {string.Join(", ", k.DocumentTypes.Keys)}");
        return 0;
    }

    public static async Task<int> ClassifierAsync(Clients c, Args a)
    {
        Printer.PrintClassifier((await c.Admin.GetClassifierAsync(a.Pos(0, "classifierId"))).Value);
        return 0;
    }

    public static async Task<int> DeleteModelAsync(Clients c, Args a)
    {
        var id = a.Pos(0, "modelId");
        await c.Admin.DeleteModelAsync(id);
        Console.WriteLine($"Deleted model {id}");
        return 0;
    }

    public static async Task<int> DeleteClassifierAsync(Clients c, Args a)
    {
        var id = a.Pos(0, "classifierId");
        await c.Admin.DeleteClassifierAsync(id);
        Console.WriteLine($"Deleted classifier {id}");
        return 0;
    }

    public static async Task<int> QuotaAsync(Clients c, Args a)
    {
        var r = (await c.Admin.GetResourceDetailsAsync()).Value;
        Console.WriteLine($"Custom document models: {r.CustomDocumentModels.Count} / {r.CustomDocumentModels.Limit}");
        return 0;
    }
}
