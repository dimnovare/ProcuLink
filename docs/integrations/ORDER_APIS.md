# ProcuLink Order Intake And Delivery APIs

This document is the working developer-facing reference for getting purchase
orders into ProcuLink and receiving supplier-ready delivery events out of
ProcuLink.

Product rule: the browser upload flow remains the primary self-service path.
All other intake channels should be positioned as assisted setup until they have
clear tenant routing, supplier routing, test-fire UX, and customer-facing setup
screens.

---

## Getting started: send your first PO

You do not need an integration to get value on day one. The fastest path is the
**browser upload** — no developer, no API key, no setup call.

### Option A — Try it with the built-in sample (fastest)

1. Sign in at `https://proculink.eu`.
2. Click **Try a sample order** on the upload screen.
3. ProcuLink loads a ready-made 3-line example purchase order, parses it, and
   walks you through review → mapping → transform → delivery.

This is the quickest way to see the whole flow without preparing a file.

### Option B — Upload your own purchase order (self-service)

1. Add a **supplier** (the company you are sending the PO to).
2. On the upload screen, drag in a **CSV or XLSX** file (PDF and XML/cXML/UBL are
   also accepted where supported).
3. ProcuLink parses the file and shows you the extracted order.
4. Review the lines. For any item code ProcuLink can't match to the supplier's
   code, enter the supplier's code once — it remembers the mapping for next time.
5. ProcuLink transforms the order into the supplier's required format.
6. Send it. If delivery isn't configured yet, ProcuLink tells you exactly what's
   missing instead of pretending it sent.

If your file doesn't parse cleanly, the easiest fix is to match the column
headers in the sample template below.

### Self-service vs assisted setup

Be honest with prospects about which channels they can turn on themselves and
which need a short setup with us:

| Channel | Mode |
|---|---|
| **Browser upload** (CSV/XLSX/PDF/XML) | **Self-service** — works today, no setup |
| **Sample order** | **Self-service** — one click |
| IMAP email polling | Self-service UI exists, Integration+ gated |
| Inbound REST API | Self-service once you create an API key, but it's a developer integration |
| Hosted inbound email webhook (`orders@…`) | **Assisted setup** — we configure DNS + routing |
| SFTP/S3 polling | **Assisted setup** — we configure credentials + supplier routing |

Rule of thumb: **browser upload is self-service; inbound REST/email/SFTP/S3 are
assisted setup** until each has its own test-credentials-and-preview screen.

### Sample CSV template

Copy the rows below into a file named `sample-order.csv` (this is the same
fixture ProcuLink uses for the built-in sample):

```csv
po_number,buyer_name,line_no,item_code,description,quantity,unit_price,currency
DEMO-2026-001,Northwind Trading OÜ,1,ACME-WIDGET-A,Widget A 10mm,12,4.50,EUR
DEMO-2026-001,Northwind Trading OÜ,2,ACME-WIDGET-B,Widget B 20mm,6,8.25,EUR
DEMO-2026-001,Northwind Trading OÜ,3,ACME-BRACKET-S,Bracket short,24,1.95,EUR
```

What each column means:

| Column | Meaning |
|---|---|
| `po_number` | Your purchase-order number. Repeat it on every line of the same order. |
| `buyer_name` | Your company name (the buyer sending the order). |
| `line_no` | Line number within the order, starting at 1. |
| `item_code` | The item code as it appears in *your* system (ProcuLink maps it to the supplier's code). |
| `description` | Free-text description of the item. |
| `quantity` | How many units you are ordering. |
| `unit_price` | Price per unit, as a plain number (e.g. `4.50`). |
| `currency` | ISO currency code, e.g. `EUR`. |

The CSV parser also accepts common header aliases (for example `po`,
`PO Number`, `qty`, `unit price`, `sku`, `buyer_code`), so an export from your
ERP will often work without renaming columns.

> TODO: ship a downloadable `.xlsx` version of this template for non-technical
> buyers who prefer Excel. For now, the CSV above can be opened and saved as
> `.xlsx` directly in Excel.

---

## Current intake options

| Channel | Customer setup | Current state | Use when |
|---|---|---|---|
| Browser upload | User uploads CSV, XLSX, PDF, XML/cXML/UBL, EDI where supported | Self-service | First pilot, manual review, fastest proof of value |
| IMAP email polling | Customer provides mailbox host, port, security, username, app password | Self-service UI exists, Integration+ gated | Customer already receives orders in a mailbox |
| Hosted inbound email webhook | ProcuLink gives an address such as `orders@{slug}.proculink.eu`; Postmark forwards messages to the API | Backend exists, assisted setup | Customer wants "just email the PO to ProcuLink" |
| Inbound REST API | Customer creates an API key and posts structured order JSON | Backend exists, API-key auth | ERP/procurement system can already send structured JSON |
| SFTP/S3 polling | Customer gives ProcuLink credentials/prefix to poll | Backend exists, assisted/internal only | Customer can only drop files to SFTP/S3 and we configure routing |

Do not present SFTP/S3 polling as fully self-service yet. The current backend
pollers can import files only when a same-organisation active default supplier is
configured on the ingress row. Unsafe configs are skipped before touching the
external storage provider and never create orders with placeholder supplier IDs.
Keep this assisted until the setup UI can test credentials, preview matched
files, and explain which supplier each file will route to.

---

## Hosted inbound email webhook

The hosted webhook flow is:

1. Customer sends an email with PO attachments to a ProcuLink-managed address.
   The intended pattern is `orders@{tenantSlug}.proculink.eu`.
2. Postmark Inbound receives the email.
3. Postmark calls `POST /api/inbound-email/postmark`.
4. ProcuLink verifies `X-Postmark-Server-Token` against
   `Inbound:Postmark:WebhookToken`.
5. ProcuLink resolves the tenant slug from the recipient address.
6. ProcuLink creates order stubs for supported attachments and enqueues parsing.

Current backend assumptions:

- Tenant mapping is config-driven:
  `Inbound:Postmark:TenantMapping:{slug} = "{organisationGuid}"`.
- Host suffix defaults to the configured inbound suffix in
  `Inbound:Postmark:HostSuffix`.
- The organisation must already have a supplier configured for inbound email.
- Unsupported or empty attachments are skipped.

Required assisted setup:

- Postmark inbound server.
- DNS/MX records for the inbound email domain.
- `Inbound:Postmark:WebhookToken` in the API environment.
- Tenant slug mapping in API configuration.
- A default supplier/routing rule for the organisation.

---

## Inbound REST API

The REST API is for structured purchase-order payloads. It is not the same as
file upload; callers send canonical-ish JSON and ProcuLink creates an order from
that structured data.

Base URL:

```text
https://api.proculink.eu
```

Authentication:

```http
X-ProcuLink-Key: plk_...
```

The API key is created in ProcuLink settings. The route slug must match the
organisation that owns the API key.

### Ping

```http
GET /api/ingress/{slug}/ping
X-ProcuLink-Key: plk_...
```

Success response:

```json
{
  "message": "ProcuLink inbound API OK",
  "slug": "nordic-distribution",
  "timestamp": "2026-06-01T10:15:30Z"
}
```

### Create order

```http
POST /api/ingress/{slug}/orders
Content-Type: application/json
X-ProcuLink-Key: plk_...
```

Request:

```json
{
  "supplierId": "Acme Components",
  "orderNumber": "PO-12345",
  "orderDate": "2026-06-01",
  "currency": "EUR",
  "notes": "Optional caller note",
  "lines": [
    {
      "buyerItemCode": "PART-001",
      "description": "Widget",
      "quantity": 100,
      "unit": "EA",
      "unitPrice": 5.5
    }
  ]
}
```

`supplierId` can be either:

- the supplier GUID, or
- the exact supplier name, case-insensitive.

Success response:

```json
{
  "id": "8fcb6240-8d7a-4a38-b011-0f39f4c21772",
  "status": "pending_review",
  "linesCount": 1
}
```

Common errors:

| Status | Meaning |
|---|---|
| `400` | Missing order lines, unknown supplier, or validation failure |
| `401` | Missing or invalid `X-ProcuLink-Key` |
| `403` | API key belongs to a different organisation slug |

Example:

```bash
curl -X POST "https://api.proculink.eu/api/ingress/nordic-distribution/orders" \
  -H "Content-Type: application/json" \
  -H "X-ProcuLink-Key: plk_example" \
  -d '{
    "supplierId": "Acme Components",
    "orderNumber": "PO-12345",
    "orderDate": "2026-06-01",
    "currency": "EUR",
    "lines": [
      {
        "buyerItemCode": "PART-001",
        "description": "Widget",
        "quantity": 100,
        "unit": "EA",
        "unitPrice": 5.50
      }
    ]
  }'
```

---

## Outbound webhooks

ProcuLink can call customer-configured webhook subscriptions for lifecycle
events. Each event includes:

```http
X-ProcuLink-Signature: sha256=<hex>
X-ProcuLink-Event: order.created
```

The signature is `HMAC-SHA256(secret, raw_payload_bytes)`.

Supported events:

| Event | When it fires |
|---|---|
| `order.created` | A new PO is uploaded or received through an intake channel |
| `order.delivered` | A supplier delivery attempt succeeds |
| `order.failed` | Delivery fails after retry handling |

Node.js verification:

```js
const crypto = require("crypto");

function verify(rawBody, signatureHeader, secret) {
  const expected = crypto
    .createHmac("sha256", secret)
    .update(rawBody)
    .digest("hex");

  return signatureHeader === `sha256=${expected}`;
}
```

---

## PDF parsing (text→LLM extraction)

ProcuLink parses **digital text-based PDFs** today. The shape is:

```text
PdfPig extracts the PDF text layer → OpenAI structures it into a canonical order → AI suggests uncertain mappings downstream
```

The PDF text layer is already exact, so we do not OCR it — PdfPig reads the
embedded text, then an OpenAI extractor turns it into a structured order
against a strict JSON schema mirroring the canonical `ParsedOrder` (PO number,
order date, buyer, currency, and per-line: line number, buyer item code,
description, quantity, unit, unit price). No supplier item code is produced by
the LLM — that is resolved downstream by the mapping engine.

Anti-hallucination safety net: every number the extractor emits must appear
verbatim in the source text, and quantity × unit price must reconcile with the
stated line amount. Suspect lines are flagged "needs review" so they surface in
`/operations/exceptions` instead of being delivered blind.

Current backend:

- `PdfPig` extracts the PDF text layer.
- The OpenAI extractor structures that text into the canonical order (primary path).
- A deterministic fixed-column `PdfOrderParser` is the **fallback** — used when
  no OpenAI key is configured, offline, or extraction fails / returns low confidence.

Required environment:

```text
Ai__OpenAI__ApiKey=<secret>
# optional — falls back to Ai__OpenAI__MappingModel, then gpt-5-mini
Ai__OpenAI__ExtractionModel=gpt-5-mini
```

The extractor is a safe **no-op** when no OpenAI key is set; the deterministic
fallback parser runs instead. Sending real customer PO data to OpenAI — text for
text-based PDFs, and rasterized page **images** for the scanned-PDF vision
fallback — requires an EU-residency OpenAI project + DPA + zero-retention.

**Scanned / image-only PDFs fall back to AI vision (review-flagged).** When
PdfPig finds no text layer, ProcuLink rasterizes the leading pages (PDFtoImage +
SkiaSharp) and sends the page images to the vision-capable OpenAI model under
the same strict schema. This needs the same `Ai__OpenAI__ApiKey` — with no key
it is a no-op and the scanned PDF fails. Because there is no text layer to
verify numbers against, **every** line from a scanned PDF is flagged "needs
review" and surfaces in `/operations/exceptions` / order review — it is
assisted, never delivered blind. A scanned PDF the vision model still can't read
fails with the clear "This PDF looks scanned or image-only — we couldn't extract
any text." message. No Azure provider is used; Azure Document Intelligence has
been removed.

**Self-hosted no-egress OCR (opt-in, enterprise).** A self-hosted OCR engine
(`RapidOcrDocumentOcrService`, backed by **RapidOcrNet** — PP-OCRv5 via ONNX
Runtime, Apache-2.0 code *and* weights, ~12 MB bundled models, in-process, no GPU,
no external network calls) is now **available** for customers who cannot send
images to OpenAI. It implements the existing `IDocumentOcrService` seam. Enabling
it is an operator action, not a self-serve UI toggle, and requires two opt-ins:

- **Globally**, set `NoEgressOcr:Enabled=true` (Railway env form
  `NoEgressOcr__Enabled=true`) on **both** the API and the Worker. This registers
  the real engine instead of the no-op. Left unset (the default), no models load
  and the deploy is byte-for-byte unchanged — it ships dormant and safe.
- **Per organisation**, set `Organisation.SelfHostedOcr=true` (DB column
  `self_hosted_ocr`) to mark that org as no-egress.

For a no-egress org, the **entire** ingest/parse pipeline is no-egress — nothing
sends that org's data to OpenAI. PDFs are routed to the deterministic parser;
scanned / image-only pages are OCR'd in-process by RapidOcrNet (no OpenAI vision);
AI SKU mapping, email-body NLP extraction, and the one-click AI schema-inference
setup tool are all gated (unresolved lines go to human review; the org uses the
manual mapping editor). There is no remaining OpenAI touchpoint in the ingest/parse
path for such an org.

The honest caveat still holds: even with self-hosted OCR there is no text layer to
verify numbers against, so **every** line from a scanned PDF is review-flagged and
surfaces in `/operations/exceptions` / order review — assisted, never delivered
blind. An illegible scan still fails with the same "scanned or image-only" message.
For non-no-egress orgs the text/vision PDF path continues to use OpenAI
(`gpt-4o-mini`) and still needs the EU-residency project + DPA + zero-retention
above. The Dockerfiles add `libgomp1` + `libfontconfig1` to the runtime stage for
this engine.
