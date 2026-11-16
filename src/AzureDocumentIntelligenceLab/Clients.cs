using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace AzureDocumentIntelligenceLab;

/// <summary>Creates the Document Intelligence clients (key or Entra ID) and the blob client lazily.</summary>
public sealed class Clients
{
    private readonly Lazy<BlobServiceClient> _blobs;

    public Settings Settings { get; }
    public DocumentIntelligenceClient Analysis { get; }                 // analyze / classify / batch
    public DocumentIntelligenceAdministrationClient Admin { get; }      // build / compose / copy / list / delete
    public BlobServiceClient Blobs => _blobs.Value;

    public Clients(Settings s)
    {
        Settings = s;
        if (s.Key is { } key)
        {
            var cred = new AzureKeyCredential(key);
            Analysis = new DocumentIntelligenceClient(s.Endpoint, cred);
            Admin = new DocumentIntelligenceAdministrationClient(s.Endpoint, cred);
        }
        else
        {
            // Needs a custom-subdomain endpoint and the "Cognitive Services User" role on the resource.
            var cred = new DefaultAzureCredential();
            Analysis = new DocumentIntelligenceClient(s.Endpoint, cred);
            Admin = new DocumentIntelligenceAdministrationClient(s.Endpoint, cred);
        }

        _blobs = new Lazy<BlobServiceClient>(() =>
            s.StorageConnectionString is { } cs ? new BlobServiceClient(cs)
            : s.StorageAccountUrl is { } url ? new BlobServiceClient(url, new DefaultAzureCredential())
            : throw new InvalidOperationException("Set STORAGE_CONNECTION_STRING (or STORAGE_ACCOUNT_URL with Entra ID). See README."));
    }

    public static DocumentIntelligenceAdministrationClient CreateAdmin(Uri endpoint, string key) =>
        new(endpoint, new AzureKeyCredential(key));
}

public static class BlobHelper
{
    public static async Task<BlobContainerClient> EnsureContainerAsync(BlobServiceClient svc, string name)
    {
        var cc = svc.GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();
        return cc;
    }

    /// <summary>
    /// Container-level SAS handed to Document Intelligence so the service can read training data
    /// (or write batch results). Uses the account key when available, otherwise a user-delegation key.
    /// </summary>
    public static async Task<Uri> GetContainerSasAsync(
        BlobServiceClient svc, string container, BlobContainerSasPermissions permissions, TimeSpan lifetime)
    {
        var cc = svc.GetBlobContainerClient(container);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expires = DateTimeOffset.UtcNow.Add(lifetime);

        if (cc.CanGenerateSasUri)
            return cc.GenerateSasUri(permissions, expires);

        UserDelegationKey key = (await svc.GetUserDelegationKeyAsync(start, expires)).Value;
        var sas = new BlobSasBuilder(permissions, expires) { BlobContainerName = container, StartsOn = start };
        return new BlobUriBuilder(cc.Uri) { Sas = sas.ToSasQueryParameters(key, svc.AccountName) }.ToUri();
    }
}

/// <summary>Locates sample-data/ whether you run from the repo root, src/ or bin/.</summary>
public static class DefaultPaths
{
    public static string SampleData { get; } = Find();
    public static string Training => Path.Combine(SampleData, "training");
    public static string Test => Path.Combine(SampleData, "test");

    private static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "sample-data");
                if (Directory.Exists(candidate)) return candidate;
            }
        return Path.Combine(Directory.GetCurrentDirectory(), "sample-data");
    }
}

/// <summary>Shared option parsing helpers.</summary>
public static class Parse
{
    public static bool IsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static SplitMode Split(string? value) => (value ?? "auto").ToLowerInvariant() switch
    {
        "auto" => SplitMode.Auto,
        "none" => SplitMode.None,
        "perpage" => SplitMode.PerPage,
        _ => throw new ArgumentException("--split must be auto, none or perPage."),
    };

    public static AnalyzeDocumentOptions AnalyzeOptions(string modelId, string fileOrUrl) =>
        IsUrl(fileOrUrl)
            ? new AnalyzeDocumentOptions(modelId, new Uri(fileOrUrl))
            : new AnalyzeDocumentOptions(modelId, BinaryData.FromBytes(File.ReadAllBytes(fileOrUrl)));
}
