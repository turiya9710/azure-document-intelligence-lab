using System.Text.Json;

namespace AzureDocumentIntelligenceLab;

/// <summary>Configuration from JSON settings files and environment variables (see README).</summary>
public sealed record Settings(
    Uri Endpoint,
    string? Key,
    string? StorageConnectionString,
    Uri? StorageAccountUrl,
    string TrainingContainer,
    string ResultsContainer)
{
    public static Settings Load()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var values = ReadJsonSettings();
        string? Value(string name) => Env(name) ?? (values.TryGetValue(name, out var value) && value.Length > 0 ? value : null);

        var endpoint = Value("DOCINTEL_ENDPOINT")
            ?? throw new InvalidOperationException("Set DOCINTEL_ENDPOINT (e.g. https://<name>.cognitiveservices.azure.com/). See README.");
        var accountUrl = Value("STORAGE_ACCOUNT_URL");

        return new Settings(
            new Uri(endpoint),
            Value("DOCINTEL_KEY"),                     // omit to use Entra ID (DefaultAzureCredential)
            Value("STORAGE_CONNECTION_STRING"),        // or STORAGE_ACCOUNT_URL + Entra ID
            accountUrl is null ? null : new Uri(accountUrl),
            Value("TRAINING_CONTAINER") ?? "docint-training",
            Value("RESULTS_CONTAINER") ?? "docint-results");
    }

    private static Dictionary<string, string> ReadJsonSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in new[] { "app.settings.json", "local.settings.json" })
        {
            var path = directories
                .Select(directory => Path.Combine(directory, fileName))
                .FirstOrDefault(File.Exists);
            if (path is null)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Settings file '{path}' contains invalid JSON: {ex.Message}", ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Settings file '{path}' must contain a JSON object.");

                AddValues(document.RootElement, settings, path);
                if (document.RootElement.TryGetProperty("Values", out var values))
                {
                    if (values.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"The 'Values' property in '{path}' must be a JSON object.");
                    AddValues(values, settings, path);
                }
            }
        }

        return settings;
    }

    private static void AddValues(JsonElement element, IDictionary<string, string> settings, string path)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (value is not null)
                    settings[property.Name] = value;
            }
            else if (property.Name is "DOCINTEL_ENDPOINT" or "DOCINTEL_KEY" or "STORAGE_CONNECTION_STRING"
                     or "STORAGE_ACCOUNT_URL" or "TRAINING_CONTAINER" or "RESULTS_CONTAINER")
            {
                throw new InvalidDataException($"Setting '{property.Name}' in '{path}' must be a string.");
            }
        }
    }
}
