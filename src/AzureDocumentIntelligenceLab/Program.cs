using Azure;
using AzureDocumentIntelligenceLab;
using AzureDocumentIntelligenceLab.Commands;

var commands = new SortedDictionary<string, (string Usage, Func<Clients, Args, Task<int>> Run)>
{
    // --- sample data prep ---------------------------------------------------------------
    ["ocr"]               = ("ocr [--dir sample-data/training] [--force]                         create <file>.ocr.json via prebuilt-layout", DataCommands.OcrAsync),
    ["upload"]            = ("upload [--dir D] [--prefix P] [--container C] [--classifier]       copy a folder to blob storage", DataCommands.UploadAsync),
    // --- prebuilt -----------------------------------------------------------------------
    ["invoice"]           = ("invoice <file|url> [--query a,b] [--hires] [--json out.json]       prebuilt-invoice incl. line items", PrebuiltCommands.InvoiceAsync),
    ["layout"]            = ("layout <file|url> [--markdown] [--kv] [--hires] [--formulas] ...   prebuilt-layout (tables, markdown for RAG)", PrebuiltCommands.LayoutAsync),
    // --- custom training ----------------------------------------------------------------
    ["train"]             = ("train <folder> <modelId> [--mode template|neural] [--max-hours N] [--overwrite] [--no-wait]", TrainingCommands.TrainAsync),
    ["train-classifier"]  = ("train-classifier <classifierId> [--base existingId] [--overwrite]   one class per classifier/<folder>", TrainingCommands.TrainClassifierAsync),
    ["compose"]           = ("compose <composedId> <classifierId> class=modelId ... [--split auto|none|perPage] [--min-class-confidence 0.7]", TrainingCommands.ComposeAsync),
    ["copy"]              = ("copy <modelId> --target-endpoint URL --target-key KEY [--target-model-id ID]", TrainingCommands.CopyAsync),
    // --- using models -------------------------------------------------------------------
    ["analyze"]           = ("analyze <modelId> <file|url> [--min-confidence 0.8] [--json out.json]  custom or composed model", AnalysisCommands.AnalyzeAsync),
    ["classify"]          = ("classify <classifierId> <file|url> [--split auto|none|perPage]", AnalysisCommands.ClassifyAsync),
    ["batch"]             = ("batch <modelId> [--prefix batch-input/] [--result-prefix P]        analyze a whole blob prefix server-side", AnalysisCommands.BatchAsync),
    ["evaluate"]          = ("evaluate <modelId> [--split test|training] [--type docType]        accuracy vs sample-data/ground-truth.json", AnalysisCommands.EvaluateAsync),
    // --- admin --------------------------------------------------------------------------
    ["models"]            = ("models                                                             list custom/composed models", AdminCommands.ModelsAsync),
    ["model"]             = ("model <modelId>                                                    details + per-field estimated accuracy", AdminCommands.ModelAsync),
    ["classifiers"]       = ("classifiers", AdminCommands.ClassifiersAsync),
    ["classifier"]        = ("classifier <classifierId>", AdminCommands.ClassifierAsync),
    ["delete-model"]      = ("delete-model <modelId>", AdminCommands.DeleteModelAsync),
    ["delete-classifier"] = ("delete-classifier <classifierId>", AdminCommands.DeleteClassifierAsync),
    ["quota"]             = ("quota                                                              custom model count vs limit", AdminCommands.QuotaAsync),
};

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("docintel - azure-document-intelligence-lab (custom models, classifiers, compose, batch, evaluate)\n");
    foreach (var (_, (usage, _)) in commands) Console.WriteLine("  " + usage);
    Console.WriteLine("\nConfig via local.settings.json / app.settings.json or environment variables:");
    Console.WriteLine("DOCINTEL_ENDPOINT, DOCINTEL_KEY (optional), STORAGE_CONNECTION_STRING | STORAGE_ACCOUNT_URL,");
    Console.WriteLine("TRAINING_CONTAINER (default docint-training), RESULTS_CONTAINER (default docint-results). See README.md.");
    return 0;
}

if (!commands.TryGetValue(args[0], out var command))
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'. Run with --help.");
    return 2;
}

try
{
    var clients = new Clients(Settings.Load());
    return await command.Run(clients, Args.Parse(args.Skip(1)));
}
catch (RequestFailedException ex)
{
    Console.Error.WriteLine($"Service error {ex.Status} {ex.ErrorCode}: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
