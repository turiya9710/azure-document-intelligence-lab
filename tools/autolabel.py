#!/usr/bin/env python3
"""
Turn a dataset that already has ground-truth field values (e.g. the Hugging Face / Kaggle invoice
datasets) into Document Intelligence labeled training data, without Studio.

For each document you need:
  <doc>.ocr.json   Layout output  (produce it with:  docintel ocr --dir <folder>)
  ground truth     {"FieldName": "value", ...}  (one JSON file for the whole set, keyed by document file name)

The script finds each value in the OCR words (exact token sequence first, then a punctuation/case
-insensitive fallback) and writes <doc>.labels.json plus fields.json.

  python autolabel.py --dir my-invoices --truth truth.json
  # truth.json: {"inv-001.png": {"InvoiceNumber": "40378170", "Total": "$1,234.50"}, ...}

Values that cannot be found, or that occur more than once, are reported - fix those by hand in
Document Intelligence Studio or edit truth.json (e.g. make the value more specific).
"""
import argparse
import json
import re
from pathlib import Path

SCHEMA_LABELS = "https://schema.cognitiveservices.azure.com/formrecognizer/2021-03-01/labels.json"
SCHEMA_FIELDS = "https://schema.cognitiveservices.azure.com/formrecognizer/2021-03-01/fields.json"


def load_words(ocr_path):
    """Return [(page_number, content, polygon_normalized)] in reading order."""
    data = json.loads(Path(ocr_path).read_text(encoding="utf-8"))
    result = data.get("analyzeResult", data)  # accept both wrapped and bare layout results
    words = []
    for page in result["pages"]:
        w, h = page["width"], page["height"]
        for word in page.get("words", []):
            poly = word["polygon"]
            norm = [poly[i] / (w if i % 2 == 0 else h) for i in range(8)]
            words.append((page["pageNumber"], word["content"], norm))
    return words


def loose(s):
    return re.sub(r"[^0-9a-z]", "", s.lower())


def find_runs(words, tokens, key):
    k = len(tokens)
    want = [key(t) for t in tokens]
    return [i for i in range(len(words) - k + 1)
            if [key(words[i + j][1]) for j in range(k)] == want]


def label_document(words, values):
    labels, problems = [], []
    for field, value in values.items():
        tokens = str(value).split()
        if not tokens:
            continue
        hits = find_runs(words, tokens, key=lambda s: s) or find_runs(words, tokens, key=loose)
        if not hits:
            problems.append(f"{field}: value {value!r} not found in OCR text")
            continue
        if len(hits) > 1:
            problems.append(f"{field}: {len(hits)} occurrences of {value!r}; labeled the first")
        i = hits[0]
        labels.append({
            "label": field,
            "key": None,
            "value": [{"page": words[i + j][0], "text": words[i + j][1], "boundingBoxes": [words[i + j][2]]}
                      for j in range(len(tokens))],
        })
    return labels, problems


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dir", required=True, help="folder with documents and their .ocr.json files")
    ap.add_argument("--truth", required=True, help="JSON: {document file name: {field: value}}")
    args = ap.parse_args()

    folder = Path(args.dir)
    truth = json.loads(Path(args.truth).read_text(encoding="utf-8"))
    field_names, total_problems = [], 0
    for name, values in truth.items():
        ocr = folder / f"{name}.ocr.json"
        if not ocr.exists():
            print(f"skip {name}: no {ocr.name} (run `docintel ocr --dir {folder}` first)")
            continue
        labels, problems = label_document(load_words(ocr), values)
        out = {"$schema": SCHEMA_LABELS, "document": name, "labels": labels}
        (folder / f"{name}.labels.json").write_text(json.dumps(out, indent=2), encoding="utf-8")
        for f in values:
            if f not in field_names:
                field_names.append(f)
        total_problems += len(problems)
        for p in problems:
            print(f"  {name}: {p}")
    fields = {"$schema": SCHEMA_FIELDS,
              "fields": [{"fieldKey": f, "fieldType": "string", "fieldFormat": "not-specified"} for f in field_names],
              "definitions": {}}
    (folder / "fields.json").write_text(json.dumps(fields, indent=2), encoding="utf-8")
    print(f"Done. {len(truth)} documents, {len(field_names)} fields, {total_problems} issues to review.")


if __name__ == "__main__":
    main()
