# ProcuLink Operator Runbook — Onboarding a Client

> Practical, code-verified setup guide. When you sign a buyer, follow this end to end.
> Every field name, route, format and reliability flag here is checked against the
> source (2026-06-05). Reliability tags: **PROD-PROVEN** (real live run) ·
> **LIVE-PROVEN** (real endpoint, test harness) · **TESTED** (unit-tested, not run
> against a real third party) · **CONFIG-GATED** (needs extra config) · **TRAP**
> (looks available but isn't — don't use).

---

## 0. The mental model

```
Order arrives  →  Parse  →  Map fields  →  Resolve exceptions  →  Transform  →  Deliver
 (inbound)       (input     (per-supplier   (SKU codes)          (output       (outbound
                  format)    mapping)                              format)       channel)
```

One **supplier** = one bundle of settings:
1. an **inbound channel** (how this buyer's orders arrive),
2. a **field mapping** (their columns → ProcuLink's canonical fields),
3. an **output format** (what the end supplier wants),
4. a **delivery channel** (how it's sent to the end supplier).

Everything is org-scoped: a logged-in user only ever touches their own org's data.

> **Plan note:** the three *pull* ingest channels (IMAP, SFTP, S3/R2) require the
> **Integration** plan. Browser upload, REST API and hosted inbound email work on
> any plan (subject to order quota).

---

## 1. Create the client's supplier(s)

UI: **`/library/suppliers`** → **"New supplier"** → enter name → **"Save supplier"**.
API: `POST /api/suppliers` with `{ "name": "Acme Components" }`.

- Name is unique per org (case-insensitive) → `409` if duplicate.
- Blocked by `429 supplier_limit_reached` / `pilot_expired` if over plan limit.
- Open the supplier at **`/library/suppliers/{id}`** — tabs: **Overview · Mappings ·
  PO Mapping · Delivery · Validation rules**. Steps 4–6 below all happen here.

Make **one supplier per distinct delivery target**. If the same buyer sends POs to
3 end-suppliers in 3 formats, that's 3 supplier records.

---

## 2. Pick the INBOUND channel (how orders arrive)

Six ways orders get in. Pick whichever fits the client. You can use more than one.

### 2.1 Browser upload — *default, zero setup*  **(PROD-PROVEN)**
- Who: anyone who can drag a file into the app.
- UI: **`/upload`** → drop file → pick **Supplier** → **"Upload & send"**.
- Accepted files: **`.csv .xlsx .pdf .xml .cxml .edi .txt`** · max **10 MB** · 20/min.
- No config. Supplier is chosen per upload.

### 2.2 Inbound REST API — *for Zapier / Make / a buyer's own system*  **(TESTED)**
- Endpoint: `POST https://api.proculink.eu/api/ingress/{slug}/orders`
- Auth header: **`X-ProcuLink-Key: plk_…`** (create the key first — see below).
- `{slug}` in the URL must match the org that owns the key.
- Body is **structured JSON, not a file**:
  ```json
  {
    "OrderNumber": "PO-1001",
    "OrderDate": "2026-06-05",
    "Currency": "EUR",
    "SupplierId": "Acme Components",      // supplier GUID *or* exact name (case-insensitive)
    "Lines": [
      { "BuyerItemCode": "SKU-1", "Description": "Widget", "Quantity": 10, "Unit": "EA", "UnitPrice": 4.50 }
    ]
  }
  ```
  (`Notes` is accepted but ignored. `Lines` must have ≥1 entry.)
- **Create the API key:** UI Settings → API Keys, or `POST /api/api-keys {"Label":"Acme Zapier"}`.
  The **raw `plk_…` key is shown once** — copy it then. List/revoke via `GET`/`DELETE /api/api-keys`.

### 2.3 Hosted inbound email — *buyer emails a PO as an attachment*  **(PROD-PROVEN)**
- Buyer sends email with a CSV/XLSX/PDF/XML/EDI attachment to a ProcuLink address.
- Routing key is the **org slug**: the backend expects `orders@{slug}.proculink.eu`.
- **Current production wiring:** Cloudflare Email Routing → Email Worker
  `proculink-inbound-email` → `POST /api/inbound-email/postmark`. The rule
  `inbound@proculink.eu` is live and maps to the org in the Worker's
  `DEFAULT_TENANT_SLUG`. (Worker source: `~/proculink-inbound-worker`; full setup +
  the multi-tenant addressing options are in `docs/live-endpoint-test-fires.md`.)
- **Requirements for it to create an order:**
  - Backend env `Inbound__Postmark__WebhookToken` must be set (it is, on Railway) and
    match the Worker secret.
  - The target org **must have at least one supplier** (no supplier → rejected). The
    order is attributed to the IMAP default supplier if set, else the org's **oldest
    active supplier**.
  - Org account status must not be `ReadOnly`/`TrialExpired`.
- **To onboard a second org onto email:** either give them
  `inbound+{theirslug}@proculink.eu` (the Worker already reads the `+slug`; add a
  matching Email Routing rule), or set up a wildcard subdomain for the native
  `orders@{slug}.proculink.eu` UX. See the live-endpoints doc.
- Accepted attachments: **`.csv .xlsx .pdf .xml .edi .txt`**. (If no usable
  attachment, it tries to read a PO out of the email body — only works with an
  OpenAI key configured.)

### 2.4 IMAP polling — *ProcuLink logs into a mailbox every 5 min*  **(LIVE-PROVEN · Integration plan)**
- UI: Settings → Email ingestion. API: `PUT /api/settings/email`.
- Fields:
  ```json
  {
    "Enabled": true,
    "Host": "imap.gmail.com",
    "Port": 993,
    "UseSsl": true,
    "Username": "po-inbox@example.invalid",
    "Password": "app-password",        // null = keep saved, "" = clear
    "Folder": "INBOX",
    "DefaultSupplierId": "<supplier-guid>"   // REQUIRED
  }
  ```
- Polls unread mail every **5 min**, imports attachments, marks them read.
- Accepted attachments: **`.csv .xlsx .pdf` only** (narrower than the others — XML/EDI
  dropped into an IMAP mailbox are silently skipped).
- Password is encrypted; responses show `********` only.

### 2.5 SFTP pull — *ProcuLink polls an SFTP folder every 5 min*  **(LIVE-PROVEN · Integration plan)**
- UI: Settings → SFTP pull. API: `PUT /api/settings/sftp`.
  ```json
  {
    "Enabled": true,
    "Host": "sftp.example.invalid",
    "Port": 22,
    "Username": "proculink",
    "Password": "…",                    // null = keep, "" = clear
    "RemoteDirectory": "/incoming/orders",
    "DefaultSupplierId": "<supplier-guid>"   // REQUIRED
  }
  ```
- Accepted: **`.csv .xlsx .pdf .xml .edi`** (no `.txt`). Dedupes by path; re-imports
  if the file content changes.
- Chrooted servers (e.g. atmoz): use a **relative** `RemoteDirectory` (`incoming`, not
  `/incoming`).

### 2.6 S3 / R2 pull — *ProcuLink watches a bucket every 5 min*  **(LIVE-PROVEN · Integration plan)**
- UI: Settings → S3/R2 pull. API: `PUT /api/settings/s3`.
  ```json
  {
    "Enabled": true,
    "BucketName": "buyer-orders",
    "KeyPrefix": "incoming/",                 // optional
    "Region": "auto",                         // "auto" for R2; AWS region otherwise
    "ServiceUrl": "https://<accountid>.r2.cloudflarestorage.com",  // REQUIRED for R2/MinIO; blank for AWS
    "AccessKeyId": "…",
    "SecretKey": "…",                         // null = keep, "" = clear
    "DefaultSupplierId": "<supplier-guid>"    // REQUIRED
  }
  ```
- Accepted: **`.csv .xlsx .pdf .xml .edi`** (no `.txt`). Dedupes by key+ETag.
- **Cloudflare R2 creds:** in the R2 dashboard create an "R2 API token"; the **Access
  Key ID** is the token id and the **Secret** is the SHA-256 of the token value. Set
  `Region=auto` and `ServiceUrl=https://<accountid>.r2.cloudflarestorage.com`.

---

## 3. INPUT formats (what files parse)

The parser is auto-selected from the file extension, with content-sniffing for XML/EDI.

| Format | Use extension | Notes | Reliability |
|---|---|---|---|
| **CSV** | `.csv` | Smart header aliasing (`po_number`/`PO Number`/`po`, `qty`, `line_no`, `sku`, `unit_price`…), delimiter sniff | PROD-PROVEN |
| **Excel** | `.xlsx` | First worksheet | TESTED |
| **PDF (text)** | `.pdf` | PdfPig extracts the text layer, then an OpenAI extractor (set `Ai:OpenAI:ApiKey`) structures it into the canonical order. Every emitted number must appear verbatim in the source and qty×price must reconcile, else the line is flagged "needs review". Without an OpenAI key (or if extraction fails/low-confidence), falls back to the deterministic column parser | TESTED |
| **PDF (scanned/image)** | `.pdf` | **Not yet supported** — image-only PDFs with no text layer fail with "This PDF looks scanned or image-only — we couldn't extract any text." A vision-LLM fallback is planned, not built. No Azure/OCR provider is used | NOT SUPPORTED |
| **cXML 1.2** | `.cxml` (or `.xml`) | OrderRequest | TESTED |
| **UBL 2.1 / Peppol** | `.xml` | Auto-detected by content; `.ubl` is **not** accepted — send as `.xml` | TESTED |
| **EDIFACT ORDERS** | `.edi` (or `.txt` sniffed) | Hand-rolled parser (no commercial EDI lib) | TESTED |
| **ANSI X12 850** | `.txt` or `.edi` **only** | ⚠️ A direct `.x12` upload is **rejected**. X12 is reached only by content-sniffing a `.txt`/`.edi` file | TESTED (routing caveat) |
| **JSON** | — | **No JSON *file* parser.** JSON orders come in only via the REST API (2.2) | n/a |

---

## 4. Field mapping (per supplier) — the **PO Mapping** tab

Map the buyer's columns to ProcuLink's canonical fields.

- **Canonical fields:** header = `PoNumber, OrderDate, BuyerName, Currency`;
  line = `LineNumber, BuyerItemCode, Description, Quantity, Unit, UnitPrice`.
- **Required to save (`*`):** `PoNumber, OrderDate, BuyerItemCode, Quantity`.
- **Fastest path — starter template:** in PO Mapping click **"Apply starter template ▾"**
  and pick one: `generic-csv`, `buyer-excel`, `cxml-orderrequest`, **`erply`**, **`directo`**.
  ⚠️ **Verify the column names against the client's real export before saving** — the
  Erply/Directo column assumptions are best-effort.
- **Or magic auto-map:** the editor detects the source columns from the most recent
  uploaded sample and AI-suggests field matches (auto-accepts ≥0.85). Accept/Edit/Reject
  each, then **Save mapping**. Use **"Re-detect"** after uploading a fresh sample.
- **Per-field transforms (manipulators):** `Replace, Trim, DateFormat, Concat, Fallback,
  Split, Multiply, Divide` (e.g. `DateFormat` to normalise `OrderDate`, `Multiply` to
  convert price units).
- **Test without saving:** `POST /api/suppliers/{id}/po-mapping/test` with a sample
  header row + line rows returns the mapped result.
- **Show standards:** the editor's "Show standards" toggle reveals the UBL/X12/EDIFACT/cXML
  code each canonical field maps to — useful when a veteran buyer asks.

### SKU mappings (buyer item code → supplier item code)
- Set on the supplier **Mappings** tab, or auto-created: when you resolve an order with
  "save mappings" on (the default), each resolved line writes an `ItemMapping` so the
  next order with that buyer code auto-resolves.
- Bulk import: `POST /api/suppliers/{id}/mappings/import` with a `buyer_code,supplier_code` CSV.

---

## 5. OUTPUT formats (what we generate)

Six reachable formats. Set the default per supplier (step 6); transform falls back to `xml`.

| Output | Set value | Reliability |
|---|---|---|
| **XML (generic)** | `xml` | PROD-PROVEN (default) |
| **CSV** | `csv` | PROD-PROVEN |
| **cXML 1.2** | `cxml` | TESTED |
| **JSON** | `json` | TESTED |
| **UBL 2.1 / Peppol** | `ubl` | TESTED |
| **ANSI X12 850** | `x12` | TESTED |

> EDIFACT *output* exists in code but is "on request" only — not self-serve. Don't promise it.

---

## 6. Pick the OUTBOUND channel — the **Delivery** tab

Set it on the supplier **Delivery** tab (`DeliveryConfigEditor`), or via
`PUT /api/suppliers/{id}/delivery-config`. Then **"Test fire"** (sends a tiny dummy CSV
and records the result) before going live. **Set the supplier's output format here too.**

Common shape of the saved config:
- `Protocol` — one of the values below
- `OutputFormat` — `xml | csv | cxml | json | ubl | x12` (what gets generated before sending)
- `AutoDeliver` — `true` = send automatically after transform; `false` = you click "Send"
- `ConfigJson` — non-secret settings (below)
- `CredentialsJson` — secrets; **write-only** (responses show `********`; send `null` to
  keep, `""` to clear, a value to replace)

> ⚠️ **`ftp` is a TRAP** — it validates but has no dispatcher and will fail at send. Use
> **`ftps`**. There is **no `webhook` protocol** — a webhook is just `http`.

### 6.1 `http` — HTTP POST (incl. webhooks)  **(PROD-PROVEN, incl. OAuth2)**
`ConfigJson`:
```json
{ "url": "https://supplier.example.com/po", "method": "POST",
  "headers": { "X-Channel": "proculink" }, "timeoutSeconds": 30 }
```
`CredentialsJson` — pick the auth `type`:
```json
{ "type": "none" }
{ "type": "bearer", "token": "abc123" }
{ "type": "apikey", "header": "X-API-Key", "value": "secret" }
{ "type": "basic", "username": "buyer", "password": "pw" }
{ "type": "oauth2_client_credentials",
  "tokenUrl": "https://idp/token", "clientId": "id", "clientSecret": "sec",
  "scope": "po.write", "authStyle": "body", "requestStyle": "form",
  "tokenResponsePath": "access_token" }
```
(OAuth2 fetches a fresh token per delivery. SSRF-guarded.)

### 6.2 `smtp` — email the artifact as an attachment  **(LIVE-PROVEN)**
`ConfigJson`:
```json
{ "host": "smtp.supplier.com", "port": 587, "useSsl": false,
  "fromAddress": "po@example.invalid", "toAddresses": ["orders@supplier.com"],
  "subjectTemplate": "PO {poNumber}", "bodyTemplate": "PO attached: {fileName}",
  "attachmentFileName": "po.csv", "timeoutSeconds": 30 }
```
`CredentialsJson`: `{ "username": "po@example.invalid", "password": "app-password" }`
(`toAddresses` may be an array or a comma-separated string. `useSsl:false` = StartTLS.
Tokens `{poNumber}`, `{fileName}`.)

### 6.3 `sftp` — upload to the supplier's SFTP  **(LIVE-PROVEN)**
`ConfigJson`:
```json
{ "host": "sftp.supplier.com", "port": 22, "remotePath": "incoming/po",
  "makeDirectories": true, "timeoutSeconds": 30 }
```
`CredentialsJson` — password **or** private key:
```json
{ "username": "buyer", "password": "pw" }
{ "username": "buyer", "privateKey": "-----BEGIN OPENSSH PRIVATE KEY-----\n…", "privateKeyPassphrase": "opt" }
```
(Chrooted servers: relative `remotePath`.)

### 6.4 `ftps` — explicit-TLS FTP upload  **(TESTED — not yet run against a real FTPS server)**
`ConfigJson`:
```json
{ "host": "ftps.supplier.com", "port": 21, "remotePath": "/in/po",
  "makeDirectories": false, "timeoutSeconds": 30, "allowInvalidCertificate": false }
```
`CredentialsJson`: `{ "username": "buyer", "password": "pw" }`
(Set `allowInvalidCertificate:true` only for self-signed test servers. **Test-fire before
trusting it with a client.**)

### 6.5 `erp_erply` — Erply REST  **(TESTED — sandbox test-fire recommended)**
`ConfigJson`: `{ "url": "https://…/api/po", "clientCode": "12345", "timeoutSeconds": 30 }`
`CredentialsJson`: `{ "type": "bearer", "token": "…" }` or `{ "type": "apikey", "header": "X-Key", "value": "…" }`

### 6.6 `erp_directo` — Directo  **(TESTED — sandbox test-fire recommended)**
`ConfigJson`: `{ "url": "https://login.directo.ee/ocra_xxx/api", "database": "buyer_db", "timeoutSeconds": 30 }`
`CredentialsJson`: `{ "user": "apiuser", "password": "pw", "key": "api-key" }`

> Note: the two ERP connectors are **not** SSRF-guarded (the http/smtp/sftp/ftps channels
> are). Only point them at endpoints you trust.

---

## 7. Run the first order (UI walkthrough)

1. **Upload** at **`/upload`** (or let it arrive via the inbound channel you set up).
   Pick the supplier → "Upload & send". → lands on `/upload/preview/{orderId}`.
2. **Review & resolve** at **`/upload/preview/{orderId}`**: accept/edit/reject AI line
   suggestions (or "Accept all"), then **Commit**. → lands on `/inbox/{orderId}`.
3. **Send** at **`/inbox/{orderId}`**: **"Send to supplier →"**. This transforms to the
   supplier's output format then delivers via the delivery channel. Use **Copy/Download**
   to grab the generated artifact.
4. **Watch status:** `parsing → pending_review/ready → transforming → ready_to_deliver →
   delivering → delivered` (or `delivery_failed` → `delivery_dead_letter` /
   `rejected_by_supplier`). The latest error shows on the order.
5. **If something breaks:** exception dashboard **`/operations/exceptions`**, operator
   health **`/operations/health`**, per-order audit `GET /api/orders/{id}/audit`. Retry
   delivery from the order, or mark it rejected if the supplier bounced it out-of-band.

> **Validation rules** (supplier "Validation rules" tab) can block delivery: error-severity
> rules with "block on fail" stop a bad order; warnings just flag it.

---

## 8. Quick checklists

**New client, file-drop only (simplest):**
1. Create supplier · 2. PO Mapping (template or magic-map, verify columns) · 3. Delivery
tab: set protocol + output format + creds, **Test fire** · 4. Upload a real PO, resolve,
Send · 5. Confirm `delivered`.

**New client, automated intake:**
- Add the inbound channel (REST key / email address / IMAP / SFTP / S3) with a
  **DefaultSupplierId** · everything else same as above. Remember pull channels need the
  **Integration** plan.

**Reliability at a glance:** browser upload, hosted email = prod-proven; IMAP/SFTP/S3
ingest + HTTP/SMTP/SFTP delivery = live-proven; FTPS + Erply + Directo = tested but
**test-fire against the real endpoint before you trust them with a client**.
