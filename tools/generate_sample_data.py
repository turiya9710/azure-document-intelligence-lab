#!/usr/bin/env python3
"""
Generate a synthetic, fully labeled sample dataset for Azure AI Document Intelligence.

Output (under --out, default ../sample-data):
  training/<docType>/<file>.pdf
  training/<docType>/<file>.pdf.labels.json   <- Document Intelligence label file (normalized polygons)
  training/<docType>/fields.json              <- field schema for the custom model
  test/<docType>/<file>.pdf                   <- held-out docs (no labels, used by `evaluate`)
  test/mixed/packet-01.pdf                    <- 3 docs merged in one PDF (classifier split demo)
  ground-truth.json                           <- expected values for every PDF (training + test)

Three document "types" (each is a distinct layout -> one custom template model each):
  invoice-contoso   supplier layout A (header left, meta block right, totals bottom right)
  invoice-fabrikam  supplier layout B (grey sidebar, different labels and date format)
  service-contract  2-page service agreement

The .ocr.json files are NOT generated here: they are the Layout API output, which the
C# app creates with `ocr` (so they match the service's own words/polygons exactly).
"""
import argparse
import json
import random
from pathlib import Path

import pdfplumber
from pypdf import PdfReader, PdfWriter
from reportlab.lib.colors import Color, black
from reportlab.lib.pagesizes import letter
from reportlab.pdfbase.pdfmetrics import getAscent, getDescent, stringWidth
from reportlab.pdfgen import canvas

W, H = letter  # 612 x 792 pt
SCHEMA_LABELS = "https://schema.cognitiveservices.azure.com/formrecognizer/2021-03-01/labels.json"
SCHEMA_FIELDS = "https://schema.cognitiveservices.azure.com/formrecognizer/2021-03-01/fields.json"

INVOICE_FIELDS = ["VendorName", "InvoiceNumber", "InvoiceDate", "DueDate", "PurchaseOrder",
                  "CustomerName", "Subtotal", "Tax", "Total"]
CONTRACT_FIELDS = ["ContractNumber", "EffectiveDate", "ProviderName", "ClientName",
                   "TermMonths", "MonthlyFee", "GoverningLaw"]


# --------------------------------------------------------------------------- drawing helpers
class Doc:
    """Thin reportlab wrapper that records the exact box of every labeled run of text."""

    def __init__(self, path: Path):
        self.c = canvas.Canvas(str(path), pagesize=letter, invariant=1)
        self.page = 1
        self.recorded = {}  # field -> list of (page, token, polygon)

    def new_page(self):
        self.c.showPage()
        self.page += 1

    def save(self):
        self.c.save()

    def text(self, x, y, s, font="Helvetica", size=10, field=None, align="left"):
        w = stringWidth(s, font, size)
        if align == "right":
            x -= w
        self.c.setFont(font, size)
        self.c.drawString(x, y, s)
        if field:
            self._record(field, x, y, s, font, size)
        return x + w

    def runs(self, x, y, parts, size=10.5):
        """parts: list of (text, font, field_or_None). Returns the final x."""
        for text, font, field in parts:
            x = self.text(x, y, text, font, size, field)
        return x

    def _record(self, field, x, y, s, font, size):
        asc = getAscent(font) / 1000 * size
        desc = -getDescent(font) / 1000 * size
        top, bottom = H - (y + asc), H - (y - desc)
        pos = 0
        for tok in s.split(" "):
            x0 = x + stringWidth(s[:pos], font, size)
            x1 = x0 + stringWidth(tok, font, size)
            pos += len(tok) + 1
            if not tok:
                continue
            poly = [x0 / W, top / H, x1 / W, top / H, x1 / W, bottom / H, x0 / W, bottom / H]
            self.recorded.setdefault(field, []).append((self.page, tok, poly))

    def labels_json(self, document_name):
        labels = []
        for field, items in self.recorded.items():
            labels.append({
                "label": field,
                "key": None,
                "value": [{"page": p, "text": t, "boundingBoxes": [poly]} for p, t, poly in items],
            })
        return {"$schema": SCHEMA_LABELS, "document": document_name, "labels": labels}


def money(v):
    return f"${v:,.2f}"


# --------------------------------------------------------------------------- random data
COMPANIES = ["Northwind Traders", "Wide World Importers", "Litware Inc", "Proseware LLC",
             "Alpine Ski House", "Coho Winery", "Lucerne Publishing", "Relecloud Corp",
             "Woodgrove Bank", "Trey Research", "Margie's Travel", "Humongous Insurance",
             "Blue Yonder Airlines", "Fourth Coffee"]
SUPPLIERS_A = ["Contoso Supplies Ltd", "Adatum Office Goods", "Tailspin Stationers", "Munson Paper Co"]
SUPPLIERS_B = ["Fabrikam Logistics GmbH", "Graphic Design Institute", "Wingtip Freight BV", "Lamna Cargo SA"]
STREETS = ["1 Main Street", "42 Harbor Road", "900 Elm Avenue", "17 Mill Lane", "230 Park Boulevard"]
CITIES = ["Seattle, WA 98101", "Austin, TX 73301", "Denver, CO 80202", "Boston, MA 02110", "Madison, WI 53703"]
ITEMS = [("Printer paper A4 (box)", 24.5), ("Toner cartridge XL", 89.9), ("Ergonomic keyboard", 59.0),
         ("USB-C docking station", 149.0), ("Whiteboard markers (12)", 12.75), ("Standing desk mat", 44.0),
         ("Pallet freight (EU)", 310.0), ("Customs clearance", 95.0), ("Warehouse handling / hr", 38.5),
         ("Cloud backup licence", 120.0), ("Courier express", 27.4), ("Labels roll 500", 9.9)]
MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
FULL_MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August",
               "September", "October", "November", "December"]
LAWS = ["State of Washington", "State of Texas", "State of New York", "State of Colorado", "Commonwealth of Massachusetts"]


def rand_date(rng):
    y = rng.choice([2025, 2026])
    return y, rng.randint(1, 12), rng.randint(1, 28)


def invoice_data(rng, layout, n):
    items = []
    for name, price in rng.sample(ITEMS, rng.randint(3, 6)):
        qty = rng.randint(1, 20)
        items.append((name, qty, price, round(qty * price, 2)))
    subtotal = round(sum(i[3] for i in items), 2)
    rate = 0.08 if layout == "A" else 0.10
    tax = round(subtotal * rate, 2)
    y, m, d = rand_date(rng)
    dm = (m % 12) + 1  # due date next month
    dy = y + (1 if m == 12 else 0)
    if layout == "A":
        vendor = rng.choice(SUPPLIERS_A)
        number = f"INV-{rng.randint(2026000, 2026999)}"
        inv_date, due_date = f"{m:02d}/{d:02d}/{y}", f"{dm:02d}/{d:02d}/{dy}"
    else:
        vendor = rng.choice(SUPPLIERS_B)
        number = f"FB-{rng.randint(10000, 99999)}"
        inv_date, due_date = f"{d} {MONTHS[m - 1]} {y}", f"{d} {MONTHS[dm - 1]} {dy}"
    po = f"PO-{rng.randint(1000, 9999)}" if rng.random() < 0.7 else None  # optional field
    return {
        "items": items, "rate": rate,
        "vendor_addr": f"{rng.choice(STREETS)}, {rng.choice(CITIES)}",
        "cust_addr": f"{rng.choice(STREETS)}, {rng.choice(CITIES)}",
        "fields": {k: v for k, v in {
            "VendorName": vendor, "InvoiceNumber": number, "InvoiceDate": inv_date, "DueDate": due_date,
            "PurchaseOrder": po, "CustomerName": rng.choice(COMPANIES),
            "Subtotal": money(subtotal), "Tax": money(tax), "Total": money(subtotal + tax),
        }.items() if v is not None},
    }


def contract_data(rng, n):
    y, m, d = rand_date(rng)
    provider, client = rng.sample(SUPPLIERS_A + SUPPLIERS_B, 1)[0], rng.choice(COMPANIES)
    return {"fields": {
        "ContractNumber": f"SA-{y}-{rng.randint(100, 999)}",
        "EffectiveDate": f"{FULL_MONTHS[m - 1]} {d}, {y}",
        "ProviderName": provider, "ClientName": client,
        "TermMonths": str(rng.choice([12, 24, 36, 48])),
        "MonthlyFee": money(rng.randrange(1500, 12000, 250)),
        "GoverningLaw": rng.choice(LAWS),
    }}


# --------------------------------------------------------------------------- layouts
def draw_invoice_a(doc, d):
    f = d["fields"]
    doc.text(54, 735, f["VendorName"], "Helvetica-Bold", 16, "VendorName")
    doc.text(54, 719, d["vendor_addr"], size=9)
    doc.text(558, 735, "INVOICE", "Helvetica-Bold", 24, align="right")
    y = 705
    for label, key in [("Invoice No:", "InvoiceNumber"), ("Invoice Date:", "InvoiceDate"),
                       ("Due Date:", "DueDate"), ("PO Number:", "PurchaseOrder")]:
        if key in f:
            doc.text(420, y, label, "Helvetica-Bold", 10)
            doc.text(558, y, f[key], "Helvetica", 10, key, align="right")
            y -= 14
    doc.text(54, 650, "Bill To:", "Helvetica-Bold", 10)
    doc.text(54, 636, f["CustomerName"], "Helvetica", 11, "CustomerName")
    doc.text(54, 622, d["cust_addr"], size=9)
    # table
    ty = 580
    doc.c.setFillColor(Color(0.9, 0.9, 0.9))
    doc.c.rect(54, ty - 5, 504, 18, fill=1, stroke=0)
    doc.c.setFillColor(black)
    for x, h, al in [(60, "Description", "left"), (360, "Qty", "right"), (450, "Unit Price", "right"), (552, "Amount", "right")]:
        doc.text(x, ty, h, "Helvetica-Bold", 10, align=al)
    y = ty - 22
    for name, qty, price, amt in d["items"]:
        doc.text(60, y, name)
        doc.text(360, y, str(qty), align="right")
        doc.text(450, y, money(price), align="right")
        doc.text(552, y, money(amt), align="right")
        y -= 16
    y -= 10
    doc.c.line(380, y + 12, 558, y + 12)
    for label, key, bold in [("Subtotal", "Subtotal", False), (f"Tax ({int(d['rate'] * 100)}%)", "Tax", False),
                             ("Total Due", "Total", True)]:
        font = "Helvetica-Bold" if bold else "Helvetica"
        doc.text(400, y, label, font, 10.5)
        doc.text(558, y, f[key], font, 10.5, key, align="right")
        y -= 16
    doc.text(54, 60, "Payment terms: net 30 days. Thank you for your business.", size=9)


def draw_invoice_b(doc, d):
    f = d["fields"]
    doc.c.setFillColor(Color(0.93, 0.95, 0.98))
    doc.c.rect(0, 0, 170, H, fill=1, stroke=0)
    doc.c.setFillColor(black)
    doc.text(18, 740, "FROM", "Helvetica-Bold", 8)
    # vendor can be long -> wrap over two lines inside the sidebar (record each line under the field)
    words, lines, cur = f["VendorName"].split(" "), [], ""
    for w in words:
        if stringWidth((cur + " " + w).strip(), "Helvetica-Bold", 11) > 140:
            lines.append(cur)
            cur = w
        else:
            cur = (cur + " " + w).strip()
    lines.append(cur)
    y = 724
    for ln in lines:
        doc.text(18, y, ln, "Helvetica-Bold", 11, "VendorName")
        y -= 14
    doc.text(18, y - 2, d["vendor_addr"].split(",")[0], size=8.5)
    doc.text(18, y - 14, d["vendor_addr"].split(",")[1].strip(), size=8.5)
    doc.text(200, 735, "TAX INVOICE", "Helvetica-Bold", 22)
    y = 700
    for label, key in [("Bill No.", "InvoiceNumber"), ("Issued", "InvoiceDate"),
                       ("Payment Due", "DueDate"), ("Customer Ref.", "PurchaseOrder")]:
        if key in f:
            doc.text(200, y, label, "Helvetica-Oblique", 9.5)
            doc.text(300, y, f[key], "Helvetica-Bold", 10.5, key)
            y -= 15
    doc.text(200, y - 8, "Client", "Helvetica-Oblique", 9.5)
    doc.text(300, y - 8, f["CustomerName"], "Helvetica-Bold", 10.5, "CustomerName")
    doc.text(300, y - 22, d["cust_addr"], size=9)
    ty = 560
    doc.c.line(200, ty + 14, 580, ty + 14)
    for x, h, al in [(200, "Service / Goods", "left"), (430, "Units", "right"), (500, "Rate", "right"), (580, "Total", "right")]:
        doc.text(x, ty, h, "Helvetica-Bold", 9.5, align=al)
    doc.c.line(200, ty - 6, 580, ty - 6)
    y = ty - 22
    for name, qty, price, amt in d["items"]:
        doc.text(200, y, name, size=9.5)
        doc.text(430, y, str(qty), size=9.5, align="right")
        doc.text(500, y, money(price), size=9.5, align="right")
        doc.text(580, y, money(amt), size=9.5, align="right")
        y -= 15
    y -= 14
    for label, key, bold in [("Net amount", "Subtotal", False), (f"VAT {int(d['rate'] * 100)}%", "Tax", False),
                             ("Amount payable", "Total", True)]:
        font = "Helvetica-Bold" if bold else "Helvetica"
        doc.text(420, y, label, font, 10)
        doc.text(580, y, f[key], font, 10, key, align="right")
        y -= 16
    doc.text(200, 50, "Bank transfer within 30 days of issue. Late fees apply.", size=8.5)


def draw_contract(doc, d):
    f = d["fields"]
    doc.text(72, 730, "SERVICE AGREEMENT", "Helvetica-Bold", 18)
    doc.runs(72, 700, [("Contract No. ", "Helvetica-Bold", None), (f["ContractNumber"], "Helvetica", "ContractNumber")])
    y = 668
    doc.runs(72, y, [("This Service Agreement is entered into as of ", "Helvetica", None),
                     (f["EffectiveDate"], "Helvetica-Bold", "EffectiveDate")])
    y -= 16
    doc.runs(72, y, [("by and between ", "Helvetica", None), (f["ProviderName"], "Helvetica-Bold", "ProviderName"),
                     (' ("Provider") and ', "Helvetica", None)])
    y -= 16
    doc.runs(72, y, [(f["ClientName"], "Helvetica-Bold", "ClientName"), (' ("Client").', "Helvetica", None)])
    y -= 32
    doc.text(72, y, "1. Services", "Helvetica-Bold", 11)
    for ln in ["Provider shall deliver the managed services described in Schedule A, including",
               "onboarding, monitoring, incident response and quarterly service reviews.",
               "Provider shall perform the services with reasonable skill and care and in accordance",
               "with industry standards."]:
        y -= 16
        doc.text(72, y, ln)
    y -= 32
    doc.text(72, y, "2. Term and Fees", "Helvetica-Bold", 11)
    y -= 16
    doc.runs(72, y, [("This Agreement has an initial term of ", "Helvetica", None),
                     (f["TermMonths"], "Helvetica-Bold", "TermMonths"),
                     (" months and renews automatically unless terminated.", "Helvetica", None)])
    y -= 16
    doc.runs(72, y, [("Client shall pay Provider a recurring monthly fee of ", "Helvetica", None),
                     (f["MonthlyFee"], "Helvetica-Bold", "MonthlyFee"), (" per month, invoiced in advance.", "Helvetica", None)])
    for ln in ["Undisputed invoices are payable within thirty (30) days of receipt. Overdue",
               "amounts accrue interest at one percent (1%) per month."]:
        y -= 16
        doc.text(72, y, ln)
    doc.new_page()
    y = 730
    doc.text(72, y, "3. Confidentiality", "Helvetica-Bold", 11)
    for ln in ["Each party shall protect the other party's confidential information using at least",
               "the same degree of care it uses for its own, and no less than reasonable care."]:
        y -= 16
        doc.text(72, y, ln)
    y -= 32
    doc.text(72, y, "4. Governing Law", "Helvetica-Bold", 11)
    y -= 16
    doc.runs(72, y, [("This Agreement is governed by the laws of the ", "Helvetica", None),
                     (f["GoverningLaw"], "Helvetica-Bold", "GoverningLaw"), (" without regard to conflict rules.", "Helvetica", None)])
    y -= 64
    doc.text(72, y, "Signed for Provider: ______________________", size=10)
    doc.text(330, y, "Signed for Client: ______________________", size=10)
    y -= 20
    doc.text(72, y, "Date: ____________", size=9)
    doc.text(330, y, "Date: ____________", size=9)


# --------------------------------------------------------------------------- dataset assembly
DOC_TYPES = {
    "invoice-contoso":  dict(prefix="ctso", fields=INVOICE_FIELDS, make=lambda r, n: invoice_data(r, "A", n), draw=draw_invoice_a, train=10, test=3),
    "invoice-fabrikam": dict(prefix="fbk",  fields=INVOICE_FIELDS, make=lambda r, n: invoice_data(r, "B", n), draw=draw_invoice_b, train=10, test=3),
    "service-contract": dict(prefix="sa",   fields=CONTRACT_FIELDS, make=lambda r, n: contract_data(r, n),       draw=draw_contract,  train=8,  test=3),
}


def fields_json(names):
    return {"$schema": SCHEMA_FIELDS,
            "fields": [{"fieldKey": n, "fieldType": "string", "fieldFormat": "not-specified"} for n in names],
            "definitions": {}}


def verify(pdf_path, labels):
    """Re-extract the text under every label box with pdfplumber; fail loudly on mismatch."""
    with pdfplumber.open(pdf_path) as pdf:
        for lab in labels["labels"]:
            for v in lab["value"]:
                page = pdf.pages[v["page"] - 1]
                x0, t, x1, _, _, b, _, _ = [c for c in v["boundingBoxes"][0]]
                box = (x0 * W - 0.5, t * H - 4, x1 * W + 0.5, b * H + 4)
                got = (page.within_bbox(box).extract_text() or "").strip()
                assert got == v["text"], f"{pdf_path.name}: label {lab['label']} expected {v['text']!r} got {got!r}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(Path(__file__).resolve().parent.parent / "sample-data"))
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()
    out = Path(args.out)
    rng = random.Random(args.seed)
    truth = {}
    first_test = {}

    for dtype, cfg in DOC_TYPES.items():
        tdir, edir = out / "training" / dtype, out / "test" / dtype
        tdir.mkdir(parents=True, exist_ok=True)
        edir.mkdir(parents=True, exist_ok=True)
        (tdir / "fields.json").write_text(json.dumps(fields_json(cfg["fields"]), indent=2))
        for split, count, folder in [("training", cfg["train"], tdir), ("test", cfg["test"], edir)]:
            for i in range(1, count + 1):
                name = f"{cfg['prefix']}-{i:04d}.pdf" if split == "training" else f"{cfg['prefix']}-t{i:03d}.pdf"
                data = cfg["make"](rng, i)
                doc = Doc(folder / name)
                cfg["draw"](doc, data)
                doc.save()
                truth[f"{split}/{dtype}/{name}"] = {"docType": dtype, "fields": data["fields"]}
                if split == "training":
                    labels = doc.labels_json(name)
                    verify(folder / name, labels)
                    (folder / f"{name}.labels.json").write_text(json.dumps(labels, indent=2))
                elif i == 1:
                    first_test[dtype] = folder / name

    # one multi-document PDF to demonstrate classifier splitting (split=auto)
    mixed = out / "test" / "mixed"
    mixed.mkdir(parents=True, exist_ok=True)
    writer, pages = PdfWriter(), []
    for dtype in ["invoice-contoso", "invoice-fabrikam", "service-contract"]:
        r = PdfReader(str(first_test[dtype]))
        for p in r.pages:
            writer.add_page(p)
        pages.append({"docType": dtype, "pages": len(r.pages)})
    with open(mixed / "packet-01.pdf", "wb") as fh:
        writer.write(fh)
    truth["test/mixed/packet-01.pdf"] = {"docType": "mixed", "segments": pages}

    (out / "ground-truth.json").write_text(json.dumps(truth, indent=2))
    n_train = sum(1 for k in truth if k.startswith("training/"))
    n_test = sum(1 for k in truth if k.startswith("test/"))
    print(f"Wrote {n_train} labeled training PDFs and {n_test} test PDFs to {out}")


if __name__ == "__main__":
    main()
