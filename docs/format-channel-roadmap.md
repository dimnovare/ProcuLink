# ProcuLink — Format and Channel Roadmap

_Date: 2026-05-28. Planning artifact, not a commitment. Source of truth for what the engine supports today, what we build in 12 months, and what we won't build._

Founder's vision: any input format, any output format, any ingress channel, one-click AI-assisted transformation, best on the planet for SME EU customers without an EDI team. This document turns that into a concrete, prioritized roadmap grounded in the code we already have.

---

## 1. Current state matrix

"Supported" = tested code paths in `ProcuLink.Transform` / `ProcuLink.Infrastructure`. "Partial" = code exists but informally (no dedicated class). "Planned" = interface exists, no implementation. "Won't-build" = explicit skip.

### 1.1 Format × direction

| Format | Input | Output | Backing files |
|---|---|---|---|
| CSV (buyer template) | Supported | Supported | `ProcuLink.Transform/Parsing/CsvOrderParser.cs`, `ProcuLink.Transform/Output/CsvTransformService.cs` |
| XLSX (buyer template) | Supported | Won't-build (Q3) | `ProcuLink.Transform/Parsing/XlsxOrderParser.cs` (input only; XLSX output deferred — suppliers do not consume XLSX in EU) |
| Text-based PDF | Supported | Won't-build (Q3) | Primary path = text→LLM extraction: PdfPig 0.1.14 pulls the PDF text layer, OpenAI structures it into canonical `ParsedOrder` (strict JSON schema, anti-hallucination checks). Deterministic `ProcuLink.Transform/Parsing/PdfOrderParser.cs` is the no-key/offline fallback. Digital text only. PDF output rejected — no supplier ingests PDF as a data interchange format. |
| Scanned PDF / image OCR | Supported via AI vision (review-flagged) | n/a | When PdfPig finds no text layer, leading pages are rasterized (PDFtoImage + SkiaSharp, both permissive; self-contained native assets, no Dockerfile change) and sent to the vision-capable OpenAI model under the same strict schema. No text layer means no number verification, so **every** line is flagged for human review (surfaces in `/operations/exceptions`) — assisted, not silent. Illegible scans still fail with the "scanned or image-only" message. Self-hosted no-egress OCR (RapidOcrNet, Apache-2.0) is now **shipped as an opt-in enterprise/config capability** (Phase 3) — for orgs marked no-egress, scanned pages are OCR'd in-process with no OpenAI call (still review-flagged). Azure Document Intelligence removed. See `docs/standards-matrix.md`. |
| cXML 1.2 | Supported | Supported | `ProcuLink.Transform/Parsing/CxmlOrderParser.cs`, `ProcuLink.Transform/Output/CxmlTransformService.cs` |
| Plain XML (generic `PurchaseOrder` envelope) | Won't-build | Supported | `ProcuLink.Transform/Output/XmlTransformService.cs`. Input is unbounded; output is fine. |
| JSON / API payload | Partial (inline `System.Text.Json` in `OrderService`) | Supported | `ProcuLink.Transform/Output/JsonTransformService.cs`. No standalone `IPurchaseOrderParser` for JSON ingress. |
| UBL 2.1 / Peppol BIS Order 3 | Planned | Planned | None. |
| EDIFACT ORDERS D96A | Planned | Planned | None. |
| EDIFACT ORDRSP / DESADV / INVOIC | Planned | Planned | None. |
| EDI X12 850 / 855 / 856 / 810 | Planned | Planned | None. |
| OAGIS BOD | Won't-build (Q1) | Won't-build (Q1) | None. Marginal demand in EU SME. |
| xCBL | Won't-build | Won't-build | None. Dead format. |
| Fixed-width | Planned | Planned | None. |
| JSONL | Planned | Planned | None. |
| XLS legacy (BIFF8 .xls) | Planned | n/a | None. Use ClosedXML or NPOI when we get there. |
| Free-text email body (LLM) | Planned | n/a | None — attachments only today. See `ProcuLink.Worker/Jobs/EmailPollingJob.cs` line 174. |

### 1.2 Channel × direction

| Channel | Inbound | Outbound | Backing files |
|---|---|---|---|
| Browser upload (multipart) | Supported | n/a | `ProcuLink.Api/Controllers/OrdersController.cs::Upload` |
| HTTP webhook out (POST artifact) | n/a | Supported | `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs` |
| ERP REST (Erply) | n/a | Supported | `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs::ErplyDeliveryDispatcher` |
| ERP form/XML (Directo) | n/a | Supported | `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs::DirectoDeliveryDispatcher` |
| IMAP poll (attachments) | Supported | n/a | `ProcuLink.Worker/Jobs/EmailPollingJob.cs` (Hangfire every 5 min, MailKit, attachment whitelist `.csv/.xlsx/.pdf`) |
| SMTP receive (`orders@{tenant}.proculink.eu`) | Planned | n/a | None. |
| Inbound REST API | Partial (`/api/orders/upload` exists, but designed for browser, not B2B push) | n/a | `OrdersController.cs` |
| SFTP pull | Planned | Planned | None. Flagged in STATUS.md "deferred until HTTP workflow is production-proven". |
| SFTP watched directory (host our SFTP, customer drops files) | Planned | n/a | None. |
| FTP / FTPS | Planned | Planned | None. |
| S3 / R2 / Azure Blob event watch | Planned | Planned | None. (R2 used internally for source-file storage; not as ingress channel.) |
| OneDrive / SharePoint / Dropbox / Google Drive OAuth folder watch | Planned | n/a | None. |
| AS2 | Won't-build (solo) | Won't-build (solo) | Requires cert process and a partnership. |
| Peppol access point | Won't-build (solo) | Won't-build (solo) | Requires accreditation. Use a hosted partner (e.g. Pagero, Storecove, B2Brouter). |
| Zapier / Make.com connector | Planned | Planned | None. |
| Shopify / WooCommerce / Magento / Cin7 webhook receiver | Planned | n/a | None. |

---

## 2. Target state matrix (12 months from 2026-05-28)

Reflects what a solo founder plus one part-time contractor can ship while keeping the existing surface stable. Items marked "via partner" are explicitly not built in-house.

### 2.1 Format target

| Format | Input | Output | Library / approach |
|---|---|---|---|
| CSV, XLSX, PDF (text), cXML 1.2, plain XML, JSON | Supported (kept) | Supported (kept) | Already done. |
| UBL 2.1 / Peppol BIS Order 3 | Supported | Supported | Hand-rolled XSD deserialization via `System.Xml.Serialization` over the published Peppol BIS 3.0 XSDs. The `UBL.NET` NuGet exists but is stale (last release 2019); writing our own is ~3 days each direction. |
| EDIFACT ORDERS D96A | Supported | Supported | `EdiFabric` (commercial, ~€1,500/yr) for ORDERS, ORDRSP, DESADV, INVOIC. Alternative: `indice-co/edi.net` (open source) — adequate for ORDERS only. Recommend EdiFabric for production. |
| EDIFACT ORDRSP / DESADV / INVOIC | Supported | Supported | Same EdiFabric license covers all four messages. ORDRSP is mandatory for supplier acknowledgement flow. |
| EDI X12 850 / 855 / 856 / 810 | Planned (post-12mo) | Planned (post-12mo) | `EdiFabric` covers X12 too. Defer because EU SME demand is low; revisit when a US-market buyer signs. |
| OAGIS BOD | Won't-build | Won't-build | Decision: skip entirely. No EU SME demand. |
| xCBL | Won't-build | Won't-build | Decision: skip. Dead format. |
| Fixed-width | Supported | Supported | Roll our own — declarative column-offset table in supplier mapping JSON; ~2 days both directions combined. Niche but every legacy distributor has at least one. |
| JSONL | Supported | Supported | Trivial — wrap `JsonTransformService` with newline-delimited iteration. |
| XLS legacy (BIFF8) | Supported | Won't-build | `NPOI` for read. XLS output is not needed. |
| Free-text email body (LLM) | Supported | n/a | Extract body text in `EmailPollingJob`, pass to OpenAI structured outputs against the `ParsedOrder` schema. The `ISchemaInferencer` slot (see §4) makes this a 1-day add once that abstraction exists. |
| Scanned PDF / image PO (OCR) | Supported via AI vision (review-flagged) | n/a | Shipped (Phase 2): no text layer → rasterize leading pages (`PDFtoImage` + `SkiaSharp`, both MIT) → vision-capable OpenAI extractor under the same strict schema; every line flagged for human review (no text to verify numbers); illegible scans still fail. Self-hosted no-egress `RapidOcrNet` (Apache-2.0, PP-OCRv5 via ONNX Runtime, in-process, no network) is now **shipped (Phase 3) as an opt-in** offline path for no-egress orgs — still review-flagged. Enabled per-org by an operator (global `NoEgressOcr:Enabled` + per-org `SelfHostedOcr`), not a self-serve toggle. (Text-based PDFs are handled via text→LLM extraction.) |

### 2.2 Channel target

| Channel | Inbound | Outbound | Library / approach |
|---|---|---|---|
| Browser upload, HTTP webhook out, Erply/Directo, IMAP poll | Kept | Kept | Already done. |
| SMTP receive (`orders@{tenant}.proculink.eu`) | Supported | n/a | `Postmark Inbound` (managed, $10/mo). Postmark POSTs parsed MIME to `/api/ingress/email/postmark`. Avoids running our own MX. `MimeKit` reused for attachment decoding. |
| Inbound REST API (`POST /api/ingress/{tenantSlug}/orders`) | Supported | n/a | New controller. Accepts raw bytes + content-type header. Webhook-style API key auth (per-tenant). Distinct from browser-Clerk upload. |
| SFTP pull | Supported | Supported | `Renci.SshNet`. Per-supplier config; cron-style poll job. |
| SFTP watched directory (we host) | Supported | n/a | `Renci.SshNet.SftpServer` (third-party) or host a Docker `atmoz/sftp` and watch via inotify-equivalent. Each tenant gets `sftp.proculink.eu/<tenantSlug>/inbox/`. |
| FTP / FTPS | Supported (low priority) | Supported (low priority) | `FluentFTP`. Build only if a paying customer asks. |
| S3 / R2 / Azure Blob event watch | Supported | Supported | `AWSSDK.S3` (R2 is S3-compatible). Customer points their bucket events at our webhook; or we poll their prefix. |
| OneDrive / SharePoint OAuth folder watch | Supported | n/a | `Microsoft.Graph` SDK. OAuth flow per tenant. |
| Dropbox / Google Drive OAuth folder watch | Supported | n/a | `Dropbox.Api` and `Google.Apis.Drive.v3`. Lower priority than OneDrive (Microsoft dominates EU SME). |
| AS2 | Via partner | Via partner | Partner: `Babelway`, `MessageXchange`, or `OpenAS2` on our own VM. Do not build AS2 from scratch — cert PKI is a tax on focus. |
| Peppol access point | Via partner | Via partner | Partner: `Storecove` (€0.30/doc), `Pagero`, or `B2Brouter`. We become a Peppol-enabled application, not an access point ourselves. |
| Zapier connector | Supported | Supported | Zapier "Public Integration" with two triggers (new order, order delivered) and two actions (upload order, set mapping). Requires Zapier approval (~6 weeks). |
| Make.com connector | Supported | Supported | Mirror of Zapier; Make approval is faster (~2 weeks). |
| Shopify / WooCommerce / Magento / Cin7 webhook receivers | Supported | n/a | Per-platform webhook adapter; each platform sends a different JSON shape, so they live in `ProcuLink.Transform/Parsing/Platforms/`. |

---

## 3. Priority ranking — top 15 next builds

Solo-senior-dev days. "Unlocks" = who pays for it. Dependencies = prerequisite roadmap items.

| # | Build | Days | Unlocks | Depends on | Library |
|---|---|---|---|---|---|
| 1 | **AI schema inference + one-click mapping wizard** (see §5) | 10 | Every customer. Removes the largest activation barrier. The single highest-leverage build. | `ISchemaInferencer` abstraction (§4) | OpenAI structured outputs (`gpt-5-mini`); reuse existing `IAiMappingService` pattern. |
| 2 | **UBL 2.1 / Peppol BIS Order 3 input** | 4 | Mid-market suppliers in EU receiving Peppol orders from public sector and large buyers. Required by Estonian, Norwegian, Dutch e-invoicing law for public-sector customers — and increasingly by private B2B. | Canonical PO unchanged. | `System.Xml.Serialization` against Peppol BIS 3.0 XSDs from `docs.peppol.eu`. No third-party NuGet. |
| 3 | **UBL 2.1 / Peppol BIS Order 3 output** | 3 | Same archetype as #2; needed for sending POs to Peppol-connected suppliers. | #2 | Same. |
| 4 | **Inbound REST API + per-tenant API keys** | 3 | Mid-market and large distributors who already have an ERP that can POST. The number-one ask from "we want to integrate" prospects. | None. | ASP.NET Core minimal API; reuse `OrderService.CreateStubAsync`. |
| 5 | **Free-text email body LLM parsing** | 2 | Small suppliers receiving free-form emails like "Please send 12× SKU-123 by Friday". This is the unique-to-ProcuLink wedge no incumbent offers. | #1 schema inferencer. | OpenAI structured outputs over `ParsedOrder` schema in `EmailPollingJob`. |
| 6 | **Postmark Inbound SMTP receive** (`orders@<slug>.proculink.eu`) | 3 | All archetypes; especially consultants onboarding their SME clients. Customers ask for "just give me an email address" within minutes of the first demo. | None. | Postmark inbound webhook ($10/mo); `MimeKit` for the inbound webhook payload. |
| 7 | **`IIngressChannel` abstraction + refactor existing four ingress paths onto it** | 3 | Internal — but blocks #6, #8, #11, and every future channel. Pay this debt now while there are only four call sites. | None. | None. |
| 8 | **SFTP pull dispatcher (outbound + inbound)** | 4 | Mid-market and large distributors. SFTP is still the #1 supplier-side integration request in EU industrial procurement. | #7 | `Renci.SshNet`. |
| 9 | **Self-hosted no-egress OCR (scanned PDF)** — ✅ **shipped (Phase 3)** | 5 | Customers who cannot send document images to OpenAI (data-residency-strict buyers). The AI vision path (`PDFtoImage` + `SkiaSharp` → OpenAI, review-flagged) ships for everyone else. | None. | Self-hosted `RapidOcrNet` (Apache-2.0) in-process offline path; opt-in per-org via operator config (`NoEgressOcr:Enabled` + `SelfHostedOcr`), ships dormant by default. |
| 10 | **EDIFACT ORDERS input** | 8 | Mid-market suppliers integrating with large EU buyer (every retail chain, every automotive tier-1). The single biggest "we cannot use you without this" gate. | #7, EdiFabric license. | `EdiFabric` (commercial). |
| 11 | **EDIFACT ORDERS output** | 4 | Same as #10 for buyer-side flows pushing to large suppliers. | #10 | EdiFabric. |
| 12 | **EDIFACT ORDRSP output** | 4 | Suppliers needing to acknowledge orders back to buyers. Always pairs with #10. | #10 | EdiFabric. |
| 13 | **Shopify webhook receiver** | 3 | E-commerce sellers using ProcuLink as their drop-ship routing layer. Modest ARPU but high volume of leads. | #7 | Shopify Admin API webhooks; HMAC signature verification. |
| 14 | **Network-effect mapping suggestions (schema fingerprint hash)** (§7) | 5 | Every customer — onboarding time drops to seconds once the library is seeded with 50+ schemas. The moat. | #1 | SHA-256 over normalized schema; Postgres unique index. |
| 15 | **Peppol via Storecove partner** | 6 (incl. contract + sandbox) | Public-sector-adjacent SME suppliers and any customer whose buyers mandate Peppol. The first "we are Peppol-enabled" claim without becoming an access point. | #2, #3 | Storecove REST API. |

Tracked but below the cut: EDIFACT INVOIC, X12 850/855, OneDrive/SharePoint, Make.com, fixed-width, JSONL, WooCommerce, Magento, Cin7, AS2 via partner.

---

## 4. Architectural decisions needed

### 4.1 `IIngressChannel` abstraction — yes, build it

Currently the four ingress paths are scattered: `OrdersController.Upload` (browser), `EmailPollingJob` (IMAP), and two not-yet-built paths (SMTP webhook, inbound API). They all converge on `IOrderService.CreateStubAsync(orgId, supplierId, stream, fileName, contentType, ct)`. That convergence is the natural seam.

Proposed interface:

```csharp
public interface IIngressChannel
{
    string ChannelName { get; }   // "browser_upload", "imap_poll", "smtp_inbound", "api_push", "sftp_pull"
    Task<IngressResult> IngestAsync(IngressEnvelope envelope, CancellationToken ct);
}

public sealed record IngressEnvelope(
    Guid OrgId,
    Guid? SupplierId,
    Stream Payload,
    string FileName,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata);  // headers, From: address, sftp path, etc.
```

This mirrors `IDeliveryDispatcher` exactly (registered as `IEnumerable<IDeliveryDispatcher>` in DI, resolved by `Protocol` name). Same pattern, same DI shape. Build it now while there are only 4 call sites — refactoring 12 call sites later costs 4× more.

### 4.2 Where do new parsers live?

Keep the current convention. `IPurchaseOrderParser` lives in `ProcuLink.Transform/Parsing/`, registered as `IEnumerable<IPurchaseOrderParser>`, selected by extension via `OrderParserFactory`. Adding `EdifactOrderParser`, `UblOrderParser`, `FixedWidthOrderParser` is mechanical.

Carve sub-namespaces only when a format becomes its own discipline: `ProcuLink.Transform/Parsing/Edi/` for the EDIFACT family. Don't sub-folder until the second file lands.

### 4.3 `ISchemaInferencer` — yes, pluggable layer

Distinct from `IAiMappingService` (SKU code suggestion). Schema inference is upstream: given a raw file, return the inferred field shape and a proposed `ParsedOrder` mapping with per-field confidence.

```csharp
public interface ISchemaInferencer
{
    Task<SchemaInferenceResult> InferAsync(Stream sample, string? fileName, string? hint, CancellationToken ct);
}

public sealed record SchemaInferenceResult(
    string SchemaFingerprint,                       // SHA-256 of normalized schema; §7 moat
    IReadOnlyList<InferredField> Fields,             // per-source-field: name, dataType, sampleValues
    IReadOnlyDictionary<string, FieldMappingProposal> CanonicalMapping,  // canonical → source field, confidence, reason
    float OverallConfidence);
```

Implementations: `OpenAiSchemaInferencer` (default), `AnthropicSchemaInferencer` (later), `DeterministicSchemaInferencer` (zero-cost fallback for known fingerprints). This layer powers §5 and §7.

### 4.4 Canonical model expansion — phased

Today the canonical model is PO-only. Expand in order:

1. **Q3 2026** — canonical **OrderConfirmation** (EDIFACT ORDRSP + cXML). Small delta on `ParsedOrder`: line acceptance status, counter-qty/price, delivery-date confirmation.
2. **Q4 2026** — canonical **Invoice** (INVOIC + Peppol BIS Invoice). Bigger delta: tax breakdown, payment terms, line discounts.
3. **Q1 2027** — canonical **ASN / DESADV**. Distinct shape — package/pallet/SSCC hierarchy.

Do not expand all four at once. Each expansion forces every parser/transformer to update; serializing keeps churn bounded. Keep parse-layer records and entity-layer split — already working per `docs/canonical-po-model.md`.

### 4.5 Avoiding DI bloat at 30+ formats

Three rules.

1. **Keyed services.** .NET 8 `services.AddKeyedSingleton<IPurchaseOrderParser, EdifactOrderParser>(".edi")`. `OrderParserFactory.GetParser(ext)` becomes `GetRequiredKeyedService<IPurchaseOrderParser>(ext)` — removes the `_parsers.FirstOrDefault` loop and the `CanParse` method on every parser.
2. **Module-scoped DI extensions.** Each format family owns one method: `services.AddEdifactParsers()`, `services.AddPeppolParsers()`. `Program.cs` calls 6 methods, not 30 registrations.
3. **No assembly scanning.** Stay explicit. Reflection-based registration is the bloat people imagine they're avoiding — keyed registrations cost two lines per parser and stay grep-able.

---

## 5. The one-click setup vision

The activation flow that ships in build #1 (10 days, two-week calendar). This is the actual differentiator.

> No-egress note: for orgs marked no-egress (`SelfHostedOcr=true`), the AI schema-inference tool (`SchemaInferenceController` / `OpenAiSchemaInferencer`) is gated off and returns empty — those orgs use the manual mapping editor — so the no-egress guarantee covers the whole ingest/parse pipeline (no OpenAI touchpoint).

### 5.1 Screen-by-screen frontend flow

1. **Drop zone (`/upload`)** — user drags a sample order file. `FileUploadZone` accepts any extension we recognize (CSV, XLSX, PDF, XML, EDIFACT, UBL, JSON, JSONL, fixed-width). Today caps at `.csv/.xlsx/.pdf` — expand the accept list.
2. **Schema preview panel** — within 4 seconds, the file streams to `POST /api/schema/infer`. The response includes the inferred field list and proposed canonical mapping. UI renders a two-column "source → canonical" mapping table with confidence badges (green ≥90%, amber 70-89%, red <70%).
3. **Confirm step (1-3 fields)** — only fields with confidence <90% appear in the confirm step. For each: a dropdown of probable canonical targets, the AI's reason, the first 3 sample values from the source file. Default selection is the AI's proposal. User clicks Confirm.
4. **Mapping saved per (buyer, supplier)** — the confirmed mapping is `PUT /api/suppliers/{id}/mapping` to the existing `SupplierPoMapping` row (extend `PoMappingConfig` to include source-field-to-canonical-field mapping alongside the existing canonical-to-output mapping).
5. **Next file is zero clicks** — on subsequent uploads for that (buyer, supplier) pair, `OrderService` computes the schema fingerprint and short-circuits the inference call. The user sees a 1-line "Mapped automatically using saved schema (matched June 3 setup)" toast.

### 5.2 Backend endpoints

```
POST   /api/schema/infer
       Body: multipart file
       Response: SchemaInferenceResult (see §4.3)

POST   /api/schema/propose-mapping
       Body: { schemaFingerprint, canonicalTarget: "po" | "invoice" | "ordrsp" }
       Response: { proposedMapping, confidence, reasonByField }
       Note: separate from /infer so users can re-propose against a different canonical target
       without re-uploading the file.

PUT    /api/suppliers/{id}/mapping
       Body: { sourceSchemaFingerprint, sourceToCanonical: {...}, version }
       Response: 204
       Persists to supplier_po_mappings.config_json (existing JSONB column).

GET    /api/suppliers/{id}/mapping/by-fingerprint/{fingerprint}
       Response: SupplierPoMapping or 404
       Used by OrderService to short-circuit inference on known schemas.
```

### 5.3 Reuses existing infrastructure

- `OpenAiMappingService` (Group E) already calls structured outputs with confidence/provenance — extend with a schema-inference prompt; do not stand up a new client.
- `SupplierPoMapping` (Group D) already stores per-supplier JSONB config — add a `sourceMapping` field alongside the canonical-to-output mapping.
- Schema fingerprint = SHA-256 over `string.Join("|", sortedColumnHeaders).ToLowerInvariant()` for tabular; over sorted JSONPath leaves for XML/JSON. Deterministic; survives row-order variation.

### 5.4 Two-week build estimate

- Days 1-2: `ISchemaInferencer` + `OpenAiSchemaInferencer` + 3 endpoints.
- Days 3-4: Extend `SupplierPoMapping` with `sourceMapping` JSONB; migration; service updates.
- Days 5-6: Frontend `SchemaPreview` + `ConfirmMappingStep` components in Bridge Layer style.
- Days 7-8: `OrderService` fingerprint short-circuit; mock-mode test coverage.
- Days 9-10: Live OpenAI QA across CSV, XLSX, PDF, cXML, UBL; edge cases (multi-sheet XLSX, header variations).

Single dev, single sprint, ship-able by day 10.

---

## 6. Honest constraints

### 6.1 Solo founder, one month (June 2026)

Realistic for one full-time developer: builds #1 (one-click wizard, 10d), #4 (Inbound REST, 3d), #7 (`IIngressChannel`, 3d), #6 (Postmark SMTP, 3d). 19 working days, 1 day slack. Anything else slips.

### 6.2 Solo founder, one quarter (June-August 2026)

Add #2+#3 (UBL/Peppol, 7d), #5 (free-text email LLM, 2d), #8 (SFTP pull, 4d), #14 (network-effect mapping, 5d). (#9 self-hosted no-egress OCR is already shipped.) Cumulative ~37 days, fits 13 weeks with buffer for support and bugfixes.

### 6.3 Solo founder, one year (June 2026-May 2027)

Add EDIFACT family (#10-12), Shopify (#13), Peppol via Storecove (#15), Make.com, fixed-width, OneDrive watch, canonical OrderConfirmation. ~110 days additional. Total ~150 days fits a year with buffer for sales, support, and production fires.

### 6.4 Requires a hire

- **AS2 implementation** — cert PKI is its own discipline. Partner instead of hiring.
- **Peppol access point accreditation** — three-month process, registered legal entity, periodic audits. Skip; integrate via Storecove ($0.30/doc). Revisit at €5M ARR.
- **EDIFACT mapping consultant** — once two customers want EDIFACT, retain a part-time EDI consultant (€80-120/hr) per buyer-supplier pair. Contracted skill, not full-time.
- **GDPR officer** — if customers ingest PII, enterprise prospects will require DPA review. Outsource initially.

### 6.5 Requires partnerships

- **Peppol** — Storecove (NL, simple REST) or B2Brouter (ES, broader EU). Pick Storecove first.
- **Zapier listing** — review takes ~6 weeks. Build the integration first, submit, then unblock the marketing claim.
- **AS2** — Babelway (BE) or MessageXchange (AU/EU). Both expose REST that abstracts AS2 cert exchange.
- **OCR for scanned PDFs** — the AI vision path (`PDFtoImage` + `SkiaSharp`, reusing the existing OpenAI extractor, review-flagged) ships (Phase 2); self-hosted no-egress `RapidOcrNet` for data-residency-strict customers is now shipped too (Phase 3, in-process, opt-in per-org). No partnership and no Azure Document Intelligence dependency.

---

## 7. The network effect layer

This is the moat — and it kicks in at ~50-100 customers, not at customer #1.

### 7.1 The mechanism

Every time a customer onboards a new buyer or supplier, they upload a sample file. `OpenAiSchemaInferencer` returns a `SchemaInferenceResult` with a `schemaFingerprint` (deterministic hash of the schema shape — column names sorted and lowercased, or sorted JSONPath leaves). The fingerprint is privacy-preserving: it contains no data values, only the structural shape.

If the fingerprint is **new**: we incur the OpenAI inference cost (~$0.002 per call), the user confirms 1-3 fields, the mapping is stored. The fingerprint and the confirmed mapping go into a shared library, **anonymized** — no orgId, no supplier name, no buyer name, no PO numbers. Only the schema shape, the canonical mapping, and the count of orgs that have confirmed this mapping.

If the fingerprint is **known**: we skip the OpenAI call. The user sees an instant proposal with confidence based on the number of orgs that previously confirmed the same mapping. At >5 orgs confirmed, confidence is treated as high enough to auto-apply with a "review" hint instead of a "confirm" gate. Cost per onboard drops from $0.002 to $0.

At 100 customers, common buyer-template schemas (Maxima, Selver, Rimi, Bauhof in Estonia; Carrefour, Lidl, Edeka in wider EU) are seen 20+ times each. The fingerprint library carries the network effect.

### 7.2 Data model sketch

```sql
CREATE TABLE shared_schema_fingerprints (
    id                  uuid PRIMARY KEY,
    fingerprint         text NOT NULL UNIQUE,         -- SHA-256 hex
    schema_kind         text NOT NULL,                 -- 'tabular' | 'xml' | 'json' | 'edifact' | 'ubl'
    inferred_canonical  text NOT NULL DEFAULT 'po',   -- 'po' | 'invoice' | 'ordrsp' | 'desadv'
    field_count         int NOT NULL,
    first_seen_at       timestamptz NOT NULL DEFAULT now(),
    last_seen_at        timestamptz NOT NULL DEFAULT now(),
    org_confirm_count   int NOT NULL DEFAULT 0         -- how many distinct orgs confirmed this
);

CREATE TABLE shared_field_mappings (
    fingerprint_id      uuid REFERENCES shared_schema_fingerprints(id) ON DELETE CASCADE,
    canonical_field     text NOT NULL,                 -- 'poNumber' | 'orderDate' | 'lines[].buyerItemCode' ...
    source_field_path   text NOT NULL,                 -- normalized JSONPath / column-name
    confidence          float NOT NULL,                -- aggregate across confirming orgs
    confirm_count       int NOT NULL DEFAULT 1,
    PRIMARY KEY (fingerprint_id, canonical_field)
);

CREATE TABLE shared_field_manipulators (
    fingerprint_id      uuid REFERENCES shared_schema_fingerprints(id) ON DELETE CASCADE,
    canonical_field     text NOT NULL,
    manipulator_name    text NOT NULL,                 -- 'DateFormat', 'Trim', 'Concat' (existing manipulator names)
    manipulator_params  jsonb,
    confirm_count       int NOT NULL DEFAULT 1,
    PRIMARY KEY (fingerprint_id, canonical_field, manipulator_name)
);
```

### 7.3 Privacy guarantees

- No tenant identifier ever lands in `shared_*` tables. The `OrgId` is dropped before insert.
- No values from the source file. Only structural shape (column names) and canonical mapping decisions.
- Column names are normalized (lowercased, stripped of whitespace) before hashing, so two customers naming a column "PO No." vs "PoNumber" still match.
- The fingerprint is one-way. Given a fingerprint, no source data can be reconstructed.
- Customers can opt out at the org level. Default is opt-in with a clear note in the onboarding tour. At enterprise tier, the default flips to opt-out.

### 7.4 Why this is a moat at 100+ customers

Three reinforcing effects.

1. **Inference cost approaches zero.** Customer #1 pays the OpenAI bill; customer #50 doesn't. Unit economics improve with scale — incumbents hand-building EDI maps see flat or worsening unit economics.
2. **Activation time approaches zero.** Customer #50 onboarding the same Maxima template their competitor onboarded last week sees the mapping pre-applied. Time-to-first-transformed-order drops from 10 minutes to 30 seconds.
3. **Defensible cold-start asset.** A competitor without 100 customers' worth of confirmed mappings cannot match the activation experience. The library is the moat — not the parsers (EdiFabric is for sale), not the dispatchers. Just the library.

### 7.5 What to build first

Build #14 — 5 days. Insert-only on first confirm; read-only short-circuit on subsequent onboardings; no privacy review needed for fingerprint-only storage. Worth shipping as soon as customer count crosses ~20.

---

## Closing note

Achievable in 12 months by one developer who refuses to gold-plate. The risk is not lack of formats — it is lack of focus. Current state covers the EU SME baseline (CSV, XLSX, PDF, cXML, JSON, HTTP, ERP REST, IMAP). The 12-month roadmap closes gaps customers actually pay for (UBL/Peppol, EDIFACT, SMTP, SFTP, OCR, free-text email, network-effect mapping). Everything else — AS2, X12, OAGIS, xCBL — deferred to a partner or skipped. Build #1 is the highest-leverage two-week project; build #7 is the cheapest debt repayment with the largest downstream payoff. Build them first.
