using Azure;
using Azure.AI.DocumentIntelligence;

namespace AzureDocumentIntelligenceLab.Commands;

/// <summary>Prebuilt models: no training needed. Good baseline before you invest in a custom model.</summary>
public static class PrebuiltCommands
{
    /// <summary>prebuilt-invoice with line items, per-field confidence and optional query-field add-on.</summary>
    public static async Task<int> InvoiceAsync(Clients c, Args a)
    {
        var opts = Parse.AnalyzeOptions("prebuilt-invoice", a.Pos(0, "file-or-url"));
        if (a.Flag("hires")) opts.Features.Add(DocumentAnalysisFeature.OcrHighResolution);
        if (a.Opt("query") is { } query)
        {
            // Query fields: ask for extra values the model doesn't return by default (add-on feature).
            opts.Features.Add(DocumentAnalysisFeature.QueryFields);
            foreach (var q in query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                opts.QueryFields.Add(q);
        }

        var op = await c.Analysis.AnalyzeDocumentAsync(WaitUntil.Completed, opts);
        var min = a.Dbl("min-confidence", 0.8);
        foreach (var d in op.Value.Documents) Printer.PrintDocument(d, min);
        if (a.Opt("json") is { } path) await File.WriteAllTextAsync(path, op.GetRawResponse().Content.ToString());
        return 0;
    }

    /// <summary>prebuilt-layout: text, tables, selection marks; --markdown gives RAG-ready output.</summary>
    public static async Task<int> LayoutAsync(Clients c, Args a)
    {
        var src = a.Pos(0, "file-or-url");
        var opts = Parse.AnalyzeOptions("prebuilt-layout", src);
        if (a.Flag("markdown")) opts.OutputContentFormat = DocumentContentFormat.Markdown;
        if (a.Flag("hires")) opts.Features.Add(DocumentAnalysisFeature.OcrHighResolution);
        if (a.Flag("kv")) opts.Features.Add(DocumentAnalysisFeature.KeyValuePairs);
        if (a.Flag("languages")) opts.Features.Add(DocumentAnalysisFeature.Languages);
        if (a.Flag("formulas")) opts.Features.Add(DocumentAnalysisFeature.Formulas);
        if (a.Flag("barcodes")) opts.Features.Add(DocumentAnalysisFeature.Barcodes);
        if (a.Flag("font")) opts.Features.Add(DocumentAnalysisFeature.FontStyling);

        var op = await c.Analysis.AnalyzeDocumentAsync(WaitUntil.Completed, opts);
        var r = op.Value;
        Console.WriteLine($"pages={r.Pages.Count} paragraphs={r.Paragraphs.Count} tables={r.Tables.Count} " +
                          $"keyValuePairs={r.KeyValuePairs.Count} languages={r.Languages.Count} styles={r.Styles.Count}");

        for (int i = 0; i < r.Tables.Count; i++)
        {
            Console.WriteLine($"  table {i + 1}: {r.Tables[i].RowCount} x {r.Tables[i].ColumnCount}");
            Printer.PrintTable(r.Tables[i]);
        }
        foreach (var kv in r.KeyValuePairs)
            Console.WriteLine($"  kv: {kv.Key.Content} => {kv.Value?.Content}  ({kv.Confidence:P0})");

        if (a.Flag("markdown"))
        {
            Directory.CreateDirectory("out");
            var path = a.Opt("out") ?? Path.Combine("out", Path.GetFileNameWithoutExtension(src) + ".md");
            await File.WriteAllTextAsync(path, r.Content);
            Console.WriteLine($"Markdown written to {path}");
        }
        return 0;
    }
}
