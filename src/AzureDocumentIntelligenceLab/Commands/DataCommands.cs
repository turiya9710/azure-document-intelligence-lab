using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AzureDocumentIntelligenceLab.Commands;

/// <summary>Prepares and uploads a labeled training set.</summary>
public static class DataCommands
{
    /// <summary>
    /// Custom training needs, next to every document, the Layout output as "&lt;file&gt;.ocr.json".
    /// Studio normally creates it; here we call prebuilt-layout ourselves and store the raw
    /// service response, which is exactly that file.
    /// </summary>
    public static async Task<int> OcrAsync(Clients c, Args a)
    {
        var root = a.Opt("dir") ?? DefaultPaths.Training;
        var force = a.Flag("force");
        var pdfs = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        Console.WriteLine($"prebuilt-layout on {pdfs.Count} documents under {root}");

        int done = 0;
        await Parallel.ForEachAsync(pdfs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (pdf, ct) =>
        {
            var target = pdf + ".ocr.json";
            if (File.Exists(target) && !force)
            {
                Console.WriteLine($"  skip {Path.GetFileName(pdf)} (ocr.json exists, use --force)");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(pdf, ct);
            var op = await c.Analysis.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(bytes), ct);
            await File.WriteAllTextAsync(target, op.GetRawResponse().Content.ToString(), ct);
            Console.WriteLine($"  ok   {Path.GetFileName(pdf)}  ({Interlocked.Increment(ref done)}/{pdfs.Count})");
        });
        return 0;
    }

    /// <summary>
    /// Uploads a folder to the training container keeping relative paths.
    /// --classifier additionally copies each PDF and its Layout result to classifier/&lt;folder&gt;/.
    /// </summary>
    public static async Task<int> UploadAsync(Clients c, Args a)
    {
        var dir = Path.GetFullPath(a.Opt("dir") ?? DefaultPaths.Training);
        var prefix = a.Opt("prefix") ?? "";
        var container = a.Opt("container") ?? c.Settings.TrainingContainer;
        var cc = await BlobHelper.EnsureContainerAsync(c.Blobs, container);

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
        var missingOcr = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !File.Exists(f + ".ocr.json")
                                          && File.Exists(f + ".labels.json")).ToList();
        if (missingOcr.Count > 0)
            Console.WriteLine($"note: {missingOcr.Count} labeled PDFs have no .ocr.json yet - run `ocr` first if you plan to train on them.");
        if (a.Flag("classifier"))
        {
            var missingClassifierOcr = files.Count(f =>
                f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !File.Exists(f + ".ocr.json"));
            if (missingClassifierOcr > 0)
                Console.WriteLine($"note: {missingClassifierOcr} PDFs have no .ocr.json; classifier training requires Layout results. Run `ocr`, then `upload --classifier` again.");
        }

        int n = 0;
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            await PutAsync(cc, file, prefix + rel);
            n++;
            if (a.Flag("classifier") && file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var cls = Path.GetFileName(Path.GetDirectoryName(file)!);
                var classifierPrefix = $"{prefix}classifier/{cls}/";
                await PutAsync(cc, file, classifierPrefix + Path.GetFileName(file));
                var ocrFile = file + ".ocr.json";
                if (File.Exists(ocrFile))
                    await PutAsync(cc, ocrFile, classifierPrefix + Path.GetFileName(ocrFile));
                n++;
                if (File.Exists(ocrFile)) n++;
            }
        }
        Console.WriteLine($"Uploaded {n} blobs to {container}/{prefix}");
        return 0;
    }

    private static Task PutAsync(BlobContainerClient cc, string localPath, string blobName)
    {
        var type = localPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/json";
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = type } };
        return cc.GetBlobClient(blobName).UploadAsync(localPath, options);
    }
}
