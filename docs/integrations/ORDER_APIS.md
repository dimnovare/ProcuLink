# ProcuLink Order Intake And Delivery APIs

This document is the working developer-facing reference for getting purchase
orders into ProcuLink and receiving supplier-ready delivery events out of
ProcuLink.

Product rule: the browser upload flow remains the primary self-service path.
All other intake channels should be positioned as assisted setup until they have
clear tenant routing, supplier routing, test-fire UX, and customer-facing setup
screens.

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
pollers can import files, but supplier resolution still needs hardening before
non-technical users can configure it safely.

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

## OCR for scanned PDFs

Yes, ProcuLink can use OCR for scanned/image-only PDFs, but the reliable shape
is:

```text
OCR engine extracts text/tables → parser normalizes → AI suggests uncertain mappings
```

Do not make the LLM the OCR engine. Use a document OCR provider first, then use
AI for field interpretation, mapping suggestions, confidence, and explanation.

Current backend:

- `PdfOrderParser` extracts text with PdfPig.
- If no text is found and OCR is configured, it calls `IDocumentOcrService`.
- `AzureDocumentIntelligenceOcrService` is the current provider.
- If `Ocr:Azure:Endpoint` or `Ocr:Azure:ApiKey` is missing, `NoOpOcrService`
  disables OCR safely.

Required environment:

```text
Ocr__Azure__Endpoint=https://<resource>.cognitiveservices.azure.com/
Ocr__Azure__ApiKey=<secret>
```

Production QA still needed:

- Configure Azure Document Intelligence in Railway.
- Upload a known scanned PO PDF.
- Verify extracted text, parsed header, line count, and failure messaging.
- Add a customer-facing "scanned PDF/OCR" status in upload/review once tested.

