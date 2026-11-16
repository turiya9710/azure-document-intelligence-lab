using System.Globalization;
using Azure.AI.DocumentIntelligence;

namespace AzureDocumentIntelligenceLab;

public static class Printer
{
    public static string Display(DocumentField f)
    {
        string? s = null;
        if (f.FieldType == DocumentFieldType.String) s = f.ValueString;
        else if (f.FieldType == DocumentFieldType.Date) s = f.ValueDate?.ToString("yyyy-MM-dd");
        else if (f.FieldType == DocumentFieldType.Time) s = f.ValueTime?.ToString();
        else if (f.FieldType == DocumentFieldType.Double) s = f.ValueDouble?.ToString(CultureInfo.InvariantCulture);
        else if (f.FieldType == DocumentFieldType.Int64) s = f.ValueInt64?.ToString(CultureInfo.InvariantCulture);
        else if (f.FieldType == DocumentFieldType.Currency && f.ValueCurrency is { } cur)
            s = $"{cur.Amount.ToString("N2", CultureInfo.InvariantCulture)} {cur.CurrencyCode}".Trim();
        else if (f.FieldType == DocumentFieldType.PhoneNumber) s = f.ValuePhoneNumber;
        else if (f.FieldType == DocumentFieldType.CountryRegion) s = f.ValueCountryRegion;
        else if (f.FieldType == DocumentFieldType.SelectionMark) s = f.ValueSelectionMark?.ToString();
        else if (f.FieldType == DocumentFieldType.Signature) s = f.ValueSignature?.ToString();
        else if (f.FieldType == DocumentFieldType.Boolean) s = f.ValueBoolean?.ToString();

        var text = (s ?? f.Content ?? "").Replace('\n', ' ');
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    public static void PrintDocument(AnalyzedDocument d, double minConfidence)
    {
        var pages = string.Join(",", d.BoundingRegions.Select(r => r.PageNumber).Distinct());
        Console.WriteLine($"  docType={d.DocumentType}  confidence={d.Confidence:P0}  pages=[{pages}]");
        foreach (var (name, field) in d.Fields)
            PrintField(name, field, minConfidence, 4);
    }

    public static void PrintField(string name, DocumentField f, double minConfidence, int indent)
    {
        var pad = new string(' ', indent);
        if (f.FieldType == DocumentFieldType.List && f.ValueList is { } items)
        {
            Console.WriteLine($"{pad}{name}: [{items.Count} items]");
            for (int i = 0; i < items.Count; i++) PrintField($"[{i}]", items[i], minConfidence, indent + 2);
            return;
        }
        if (f.FieldType == DocumentFieldType.Dictionary && f.ValueDictionary is { } dict)
        {
            Console.WriteLine($"{pad}{name}:");
            foreach (var (key, value) in dict) PrintField(key, value, minConfidence, indent + 2);
            return;
        }

        var conf = f.Confidence;
        var confText = conf is { } c ? c.ToString("0%", CultureInfo.InvariantCulture) : "n/a";
        var flag = conf is { } c2 && c2 < minConfidence ? "   <-- below threshold, route to human review" : "";
        Console.WriteLine($"{pad}{name,-22} {Display(f),-34} {confText,5}{flag}");
    }

    public static void PrintTable(DocumentTable t)
    {
        var grid = new string?[t.RowCount, t.ColumnCount];
        foreach (var cell in t.Cells) grid[cell.RowIndex, cell.ColumnIndex] = cell.Content.Replace('\n', ' ');
        for (int r = 0; r < t.RowCount; r++)
        {
            var row = Enumerable.Range(0, t.ColumnCount).Select(c => (grid[r, c] ?? "").PadRight(18)[..18]);
            Console.WriteLine("    | " + string.Join(" | ", row) + " |");
        }
    }

    public static void PrintModel(DocumentModelDetails m)
    {
        var kind = m.BuildMode?.ToString() ?? (m.ClassifierId is null ? "custom" : "composed");

        Console.WriteLine($"Model {m.ModelId}  kind={kind}  created={m.CreatedOn:u}  trainingHours={m.TrainingHours}");
        if (m.ClassifierId is not null) Console.WriteLine($"  classifier={m.ClassifierId}  split={m.Split}");
        if (m.Tags.Count > 0) Console.WriteLine("  tags: " + string.Join(", ", m.Tags.Select(t => $"{t.Key}={t.Value}")));

        foreach (var (docType, dt) in m.DocumentTypes)
        {
            Console.WriteLine($"  docType '{docType}'" + (dt.ModelId is null ? "" : $" -> model {dt.ModelId}"));
            foreach (var (field, schema) in dt.FieldSchema)
            {
                // FieldConfidence is the service's *estimated accuracy* for the field after training.
                var est = dt.FieldConfidence.TryGetValue(field, out var conf) ? conf.ToString("0%", CultureInfo.InvariantCulture) : "n/a";
                Console.WriteLine($"    {field,-20} {schema.FieldType,-10} estimated accuracy {est}");
            }
        }
        foreach (var w in m.Warnings) Console.WriteLine($"  warning {w.Code}: {w.Message}");
    }

    public static void PrintClassifier(DocumentClassifierDetails c)
    {
        Console.WriteLine($"Classifier {c.ClassifierId}  created={c.CreatedOn:u}  base={c.BaseClassifierId ?? "-"}");
        foreach (var (name, _) in c.DocumentTypes) Console.WriteLine($"  class '{name}'");
        foreach (var w in c.Warnings) Console.WriteLine($"  warning {w.Code}: {w.Message}");
    }
}
