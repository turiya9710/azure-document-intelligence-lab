# azure-document-intelligence-lab

A hands-on C# lab for Azure AI Document Intelligence: custom models, classifiers, composed models, batch and evaluation.

A console app (`docintel`) that walks the whole Document Intelligence lifecycle on included sample data:
prebuilt models, custom template/neural training, a document classifier, composed models, batch analysis,
cross-resource model copy, and an accuracy evaluator that compares model output with ground truth.

- SDK: `Azure.AI.DocumentIntelligence` 1.0.0 (REST API `2024-11-30`, v4.0 GA), .NET 10
- Sample data: 28 labeled training PDFs + 10 test PDFs, three layouts (two invoice templates, one 2-page contract), fully synthetic

## What maps to what

| Feature | Command | Code |
|---|---|---|
| Prebuilt invoice, line items, per-field confidence, query-field add-on | `invoice` | `PrebuiltCommands.InvoiceAsync` |
| Layout to Markdown (RAG), tables, key-value pairs, high-res OCR, formulas, barcodes | `layout` | `PrebuiltCommands.LayoutAsync` |
| Generate `.ocr.json` (Layout output) required for training | `ocr` | `DataCommands.OcrAsync` |
| Upload labeled data / classifier folders | `upload` | `DataCommands.UploadAsync` |
| Custom **template** or **neural** model build, tags, max training hours | `train` | `TrainingCommands.TrainAsync` |
| Custom **classifier** (one folder = one class), incremental training via `--base` | `train-classifier` | `TrainingCommands.TrainClassifierAsync` |
| **Composed model** (classifier + one extractor per class, auto-split of multi-document PDFs, class-confidence gate) | `compose` | `TrainingCommands.ComposeAsync` |
| **Copy** a model to another resource/region | `copy` | `TrainingCommands.CopyAsync` |
| Analyze with custom/composed model, low-confidence flagging | `analyze` | `AnalysisCommands.AnalyzeAsync` |
| Classify / split a mixed PDF packet | `classify` | `AnalysisCommands.ClassifyAsync` |
| **Batch** analysis of a blob prefix | `batch` | `AnalysisCommands.BatchAsync` |
| **Evaluate**: per-field accuracy, confidence calibration, coverage vs precision, routing accuracy | `evaluate` | `AnalysisCommands.EvaluateAsync` |
| List/inspect/delete models and classifiers, quota | `models`, `model`, `classifiers`, `delete-model`, `quota` ... | `AdminCommands` |

## Setup

1. Create a Document Intelligence resource (S0) and a storage account. `infra/provision.sh` does it with the Azure CLI and prints the env vars. Pick a region that supports custom neural models (check the docs), and delete the resource group when done.
2. Set environment variables:
   ```bash
   export DOCINTEL_ENDPOINT="https://<name>.cognitiveservices.azure.com/"
   export DOCINTEL_KEY="<key>"                       # omit to use Entra ID (needs the 'Cognitive Services User' role)
   export STORAGE_CONNECTION_STRING="<connection string>"   # or STORAGE_ACCOUNT_URL + Entra ID (Storage Blob Data Contributor)
   # optional: TRAINING_CONTAINER (default docint-training), RESULTS_CONTAINER (default docint-results)
   ```
3. `cd src/AzureDocumentIntelligenceLab && dotnet run -- --help` (the executable is named `docintel`; the commands below use `dotnet run -- <command>`).

Training reads your container through a short-lived SAS the app generates (account key or user-delegation key).

## Walkthrough

Run from `src/AzureDocumentIntelligenceLab`; `dotnet run -- <command>`.

```bash
# 1. Prebuilt baseline - no training
dotnet run -- invoice ../../sample-data/test/invoice-fabrikam/fbk-t001.pdf
dotnet run -- layout  ../../sample-data/test/service-contract/sa-t001.pdf --markdown

# 2. Prepare + upload training data (.ocr.json is created by the service's Layout model)
dotnet run -- ocr
dotnet run -- upload --classifier

# 3. One custom model per layout (template = fixed layouts; use --mode neural for variable layouts)
dotnet run -- train invoice-contoso  model-contoso
dotnet run -- train invoice-fabrikam model-fabrikam
dotnet run -- train service-contract model-contract
dotnet run -- model model-contoso            # estimated accuracy per field after training

# 4. Classifier, then compose everything into one endpoint
dotnet run -- train-classifier doc-classifier
dotnet run -- compose composed-docs doc-classifier \
    invoice-contoso=model-contoso invoice-fabrikam=model-fabrikam service-contract=model-contract

# 5. Use it - the packet contains 3 different documents in one PDF
dotnet run -- classify doc-classifier ../../sample-data/test/mixed/packet-01.pdf
dotnet run -- analyze  composed-docs  ../../sample-data/test/mixed/packet-01.pdf

# 6. Measure it
dotnet run -- evaluate model-contoso --type invoice-contoso
dotnet run -- evaluate composed-docs

# 7. Scale / operate
dotnet run -- upload --dir ../../sample-data/test --prefix batch-input/
dotnet run -- batch composed-docs --prefix batch-input/
dotnet run -- copy model-contoso --target-endpoint https://<other>.cognitiveservices.azure.com/ --target-key <key>
dotnet run -- quota
```

Things to try: retrain with `--mode neural`, delete two training PDFs and watch `evaluate` change, add an "other" class
folder under `classifier/`, raise `--min-class-confidence` in `compose`.

## Sample data (`sample-data/`)

```
training/<docType>/*.pdf, *.pdf.labels.json, fields.json    docTypes: invoice-contoso (10), invoice-fabrikam (10), service-contract (8)
test/<docType>/*.pdf                                        3 held-out docs per type
test/mixed/packet-01.pdf                                    invoice + invoice + contract merged (classifier split demo)
ground-truth.json                                           expected values for every PDF
```

Both invoice layouts share one schema (`VendorName`, `InvoiceNumber`, `InvoiceDate`, `DueDate`, `PurchaseOrder` (optional,
~70% of docs), `CustomerName`, `Subtotal`, `Tax`, `Total`) but use different labels, positions and date formats, which is
exactly the case composed models solve. The contract has 7 fields. Regenerate with
`pip install -r tools/requirements.txt && python tools/generate_sample_data.py [--seed N]`; the generator verifies
every label box against the PDF text before writing it.

## Public datasets for realistic data

| Dataset | What it is | Where |
|---|---|---|
| Invoices (Sparrow / Katana ML) | ~500 synthetic invoice images with ground-truth JSON, MIT | Hugging Face `katanaml-org/invoices-donut-data-v1` |
| High Quality Invoice Images for OCR | Synthetic invoices, part annotated | Kaggle `osamahosamabdellatif/high-quality-invoice-images-for-ocr` (HF port: `Voxel51/high-quality-invoice-images-for-ocr`) |
| CUAD | 510 commercial contracts, 41 clause categories, CC BY 4.0 | HF `theatticusproject/cuad-qa`; raw files `github.com/TheAtticusProject/cuad` (data.zip), Zenodo 4595826 |
| SROIE | ~1,000 scanned receipts with company/address/date/total | Kaggle `urbikn/sroie-datasetv2`, HF `Voxel51/scanned_receipts` |

To train on one of these: export the images to a folder, write a `truth.json` (`{"file.png": {"InvoiceNumber": "...", ...}}`)
from the dataset's ground truth, then:

```bash
dotnet run -- ocr --dir my-invoices                          # creates <file>.ocr.json (Layout works on images too)
python tools/autolabel.py --dir my-invoices --truth truth.json   # writes .labels.json + fields.json, reports unmatched values
dotnet run -- upload --dir my-invoices --prefix my-invoices/
dotnet run -- train my-invoices model-my-invoices --mode neural
```

Use a dataset's own licence terms; CUAD is real contract text, so label the clause spans you care about rather than expecting a fixed form.

## Notes and limits

- **Not compiled or run against Azure in the sandbox that produced this** (NuGet and Azure endpoints were unreachable there). The code was checked against the 1.0.0 public API listing and syntax-parsed; expect at most small fixes on first `dotnet build`.
- Label/`fields.json` formats follow the schema used by Studio-generated files (normalized 0-1 polygons, one entry per word). If a build is rejected, label one document in Document Intelligence Studio and diff it against `sample-data/training/.../*.labels.json`.
- `.ocr.json` is saved as the raw final Layout response; this matches what Studio stores, but verify on first build.
- All custom fields are typed `string` for robustness. Typed fields (date, number/currency) and table/array fields (line items) are best set up in Studio; `invoice` already returns line items.
- Neural builds are billed per training hour beyond a free allotment; use `--max-hours` and check current pricing. `delete-model` and `delete-classifier` clean up; custom model count is capped per resource (`quota`).
- Models built with the 2024-11-30 API are not portable to older API versions, and composed models require the classifier-based flow used here.

## License

MIT. Sample data is synthetic and generated by `tools/generate_sample_data.py`.
