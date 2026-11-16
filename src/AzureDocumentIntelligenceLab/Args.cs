using System.Globalization;

namespace AzureDocumentIntelligenceLab;

/// <summary>Tiny argument parser: positionals plus --name value options and --flag booleans.</summary>
public sealed class Args
{
    // Options that never take a value, so "--markdown file.pdf" keeps file.pdf positional.
    private static readonly HashSet<string> BoolFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "markdown", "hires", "kv", "languages", "formulas", "barcodes", "font",
        "overwrite", "force", "classifier", "no-wait",
    };

    private readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public static Args Parse(IEnumerable<string> tokens)
    {
        var a = new Args();
        var list = tokens.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) { a.Positional.Add(list[i]); continue; }
            var key = list[i][2..];
            if (BoolFlags.Contains(key)) a._opts[key] = "true";
            else if (i + 1 < list.Count) a._opts[key] = list[++i];
            else throw new ArgumentException($"Option --{key} needs a value.");
        }
        return a;
    }

    public string? Opt(string name) => _opts.TryGetValue(name, out var v) ? v : null;
    public bool Flag(string name) => _opts.ContainsKey(name);

    public string Pos(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new ArgumentException($"Missing required argument <{name}>.");

    public double Dbl(string name, double fallback) =>
        Opt(name) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : fallback;
}
