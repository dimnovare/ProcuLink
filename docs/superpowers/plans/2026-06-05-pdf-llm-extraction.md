# Plan: PDF parsing → text→LLM structured extraction (replace Azure OCR + brittle regex)

**Status:** DECIDED 2026-06-05. Build handed to a chip (multi-agent). This doc is the
self-contained spec — read it fully before coding.

## Why (evidence)
We benchmarked **22 real Markit POs + invoices** (Danfoss, ABB, REDACTED-PARTY, Veolia,
Aperam, REDACTED-PARTY, Siemens, Continental, REDACTED-PARTY, Rheinbahn, ANDRITZ, REDACTED-PARTY, LähiTapiola,
Somfy, BeCom, UFP, CEVA, DNV…) through `PdfPig-text → gpt-5-mini (strict JSON schema) →
structured order`:

- **22/22 parsed, 60 line items.**
- **177/177 numbers (100%) found verbatim in source** (no hallucinated numbers).
- **57/58 (98.3%) qty×unit_price = line_amount.** The 2 misses were correctly auto-flagged
  (a gross-vs-net amount, a missing qty) — the validation routes them to review, not silent error.
- Languages EN/DE/FR/PL/FI; currencies EUR/NOK/DKK/CZK/PLN/GBP; multi-page (7-page DE invoice → 11 lines);
  per-line dates; decimal-comma; shipping lines — **all zero-shot, one code path, no templates.**
- **20/20 vendors send DIGITAL-text PDFs; 0 scanned.** OCR is the edge case, not the main event.

Reusable benchmark harness lives at `~/pl_bench.py` (points at `~/Downloads/POs`); re-run to validate.

The current `PdfOrderParser` uses fixed-column regexes (`line code desc qty unit price`, one line)
that fail on **every** real sample (multi-line records, varied columns, languages). The DocParser-style
per-vendor template model is the thing we're escaping. **Text→LLM is the most flexible solution and is
cheaper** (~€0.0005/doc on the OpenAI we already pay for; it lets Markit drop DocParser.com + Azure DI).

## Decision
1. **Primary PDF path = text→LLM structured extraction**, producing the canonical `ParsedOrder`.
2. **Vision LLM = fallback** only when there is no text layer (rare). Phase 2.
3. **Self-hosted OCR (RapidOcrNet, Apache-2.0) = no-egress fallback** for customers who forbid sending data to OpenAI. Phase 3.
4. **Remove Azure Document Intelligence** entirely.
5. Keep the existing regex `PdfOrderParser` as a last-resort fallback when the LLM is unavailable (no API key) so dev/tests/offline still work.

---

## Architecture (verified against code)

### Canonical contract the LLM must emit — mirror `ParsedOrder` exactly
- `ParsedOrder` — `ProcuLink.Transform/Parsing/ParsedOrder.cs:7`: `PoNumber string?`, `OrderDate DateTime?`, `BuyerName string?`, `Currency string?`, `Lines`.
- `ParsedOrderLine` — `ProcuLink.Transform/Parsing/ParsedOrderLine.cs:8`: `LineNumber int`, `BuyerItemCode string` (NON-null), `Description string?`, `Quantity decimal` (NON-null), `Unit string?`, `UnitPrice decimal?`.
- **Do NOT emit `SupplierItemCode`** — it is resolved downstream from `item_mappings` + AI in `BuildLineEntitiesAsync`. Anything emitted there is ignored.
- Core mirror for the no-source-file path: `ExtractedOrder`/`ExtractedOrderLine` — `ProcuLink.Core/Services/Ai/IEmailBodyOrderExtractor.cs:8,22` (field-identical; Core can't reference Transform).

### Where it plugs in (the org-id wrinkle — important)
`IPurchaseOrderParser.ParseAsync(Stream, ct)` carries **no `organisationId`**, but the LLM extractor needs it for the per-org token cap (`IAiUsageTracker`, `Ai:OpenAI:MonthlyTokenLimitPerOrg`). So **do NOT call the LLM from inside the singleton parser.** Route it from the parse orchestrator where `organisationId` is in scope:

- File-upload path: `OrderService.ParseStoredFileAsync(organisationId, orderId, ct)` (`OrderService.cs:413`) → after it loads the buffer + detects format, for `.pdf`:
  1. If `IStructuredOrderExtractor.IsAvailable` → `ExtractAsync(buffer, "application/pdf", organisationId, ct)`.
  2. On `Success` with ≥1 line → convert `ExtractedOrder → ParsedOrder` and continue the **existing** downstream unchanged (`BuildLineEntitiesAsync` at `OrderService.cs:530` does deterministic + AI SKU mapping; status → `pending_review`/`ready`; persistence; audit; fingerprint).
  3. Else fall back to `parserFactory.GetParser(".pdf")` → `PdfOrderParser` (regex/PdfPig) as today.
- This reuses ALL downstream: `BuildLineEntitiesAsync`, status transitions, persistence (`OrderService.cs:541-596`), audit, `order.created` trigger, schema-fingerprint.

### New seam (Core)
```csharp
namespace ProcuLink.Core.Services.Ai; // or .Ocr — chip's call
public interface IStructuredOrderExtractor
{
    bool IsAvailable { get; }
    Task<StructuredExtractionResult> ExtractAsync(Stream document, string contentType, Guid organisationId, CancellationToken ct);
}
public sealed record StructuredExtractionResult(bool Success, double Confidence, ExtractedOrder? Order, string? FailureReason);
```
Implementation `OpenAiPdfOrderExtractor` (Infrastructure) — copy the structure of
`OpenAiEmailBodyOrderExtractor` (`ProcuLink.Infrastructure/Services/Ai/OpenAiEmailBodyOrderExtractor.cs`)
and `OpenAiMappingService` (`ProcuLink.Infrastructure/Services/OpenAiMappingService.cs`):
- **OpenAI SDK** `OpenAI` 2.10.0 (already referenced in `ProcuLink.Infrastructure.csproj:18`). `using OpenAI.Chat;`.
- `ChatClient` built only when `Ai:Provider=="openai"` && `Ai:OpenAI:ApiKey` present; else `IsAvailable=false`, return `Success=false` (no-op — never throws). Mirrors `OpenAiMappingService.cs:117-127`.
- **Strict JSON schema**: `ChatResponseFormat.CreateJsonSchemaFormat(name, BinaryData schema, jsonSchemaIsStrict: true)` — `OpenAiMappingService.cs:243-249`. Schema = `static readonly BinaryData` raw `u8` literal; **every property in `required` + `additionalProperties:false` at every object level** (strict-mode rule). Schema = the `ParsedOrder` field set above.
- **Text-first (Phase 1)**: extract text with PdfPig (reuse the y-bucket word-clustering from `PdfOrderParser.cs:94-119` so columns survive in reading order), send as a `UserChatMessage(text)`. *No rasterization, no native deps, Dockerfile unchanged.*
- **Usage cap**: reuse `IAiUsageTracker`. If the new service is **Singleton** (registerable in BOTH Api+Worker), use the `IServiceScopeFactory` per-call pattern from `OpenAiMappingService.cs:110-115,190-217,254-269` (do NOT depend on `ICurrentTenantService` — that's HttpContext-only and breaks the Worker host; see `Worker/Program.cs:170-173`). Pre-flight `IsAtOrOverLimitAsync`, post-call `IncrementAsync(orgId, completion.Usage?.TotalTokenCount ?? 0)`.
- **Model**: add config key `Ai:OpenAI:ExtractionModel` with fallback `?? Ai:OpenAI:MappingModel ?? "gpt-5-mini"`. (Prod `MappingModel` is `gpt-4o-mini`; both support vision later.)
- **Anti-hallucination validation** (the safety net that makes this trustworthy for money):
  - After extraction, verify every emitted numeric (`quantity`, `unit_price`, `line_amount` if present, totals) appears as a digit-sequence in the source text (normalize: strip non-digits, unify `,`/`.`). Numbers not found → drop/flag.
  - `qty × unit_price ≈ line_amount` (tolerance `max(0.02*amount, 0.05)`); mismatch → the line is marked `NeedsReview` (so it surfaces in `/operations/exceptions`).
  - Map extraction confidence onto line `Confidence`/`NeedsReview` so uncertain cells become reviewable, never silent.
- Low confidence / no lines → return `Success=false` so the orchestrator falls back to regex (and `ParseStoredFileAsync` already treats 0 lines as a failure → existing `ParseFailedPanel`).

### DI changes (BOTH hosts)
- `ProcuLink.Api/Program.cs` ~L278-301 and `ProcuLink.Worker/Program.cs` ~L119-183.
- Remove the `Ocr:Azure:*`-gated `AzureDocumentIntelligenceOcrService` branch; keep `IDocumentOcrService` + `NoOpOcrService` (used by `PdfOrderParser` ctor; repurpose for the Phase-3 self-hosted engine).
- Register `IStructuredOrderExtractor → OpenAiPdfOrderExtractor` (Singleton + scope-factory, so it works in Api **and** Worker — the Worker runs `ParseOrderJob`).
- Wire it into `OrderService` (constructor inject `IStructuredOrderExtractor`).

### Remove (Azure)
- Delete `ProcuLink.Infrastructure/Services/Ocr/AzureDocumentIntelligenceOcrService.cs` + `ProcuLink.Infrastructure.Tests/Services/Ocr/AzureDocumentIntelligenceOcrServiceTests.cs`.
- Remove `Azure.AI.DocumentIntelligence` (`ProcuLink.Infrastructure.csproj:19`) + its `using`s.
- Remove `Ocr:Azure:*` reads from both `Program.cs`. (No `appsettings` Ocr section exists — nothing to delete there.)

---

## Phasing
- **Phase 1 (ship first — the 80/20):** text→LLM extractor (primary) + orgId/usage-cap + validation + ExtractedOrder→ParsedOrder + downstream reuse + regex fallback + remove Azure + tests. No native deps, no Dockerfile change, no migration.
- **Phase 2 ✅ SHIPPED 2026-06-05:** vision fallback for scanned/no-text PDFs — when PdfPig finds no text, rasterize the leading pages via **PDFtoImage (MIT) + SkiaSharp (MIT)** (self-contained NuGet native assets, Debian-OK, **no Dockerfile / system-package change** — verified loading on the `aspnet:8.0` base via a Docker probe; **never** Ghostscript/Magick.NET/poppler — AGPL/GPL) and extract via the vision-capable OpenAI model with the same strict schema. All scanned-PDF lines are flagged for human review (no text layer to verify numbers against — assisted, not auto-delivered); illegible scans still fail with the "scanned or image-only" message. Live-verified end to end (image-only PDF → vision → structured order on `gpt-4o-mini`).
- **Phase 3 ✅ SHIPPED 2026-06-05:** self-hosted no-egress OCR — **RapidOcrNet 2.0.0** (PP-OCRv5 via ONNX Runtime, Apache-2.0 code+weights, ~12 MB bundled models, in-process, no GPU, no external network) implements `IDocumentOcrService` (replacing the no-op). Opt-in via global `NoEgressOcr:Enabled` (registers the real engine on API + Worker; unset in prod → `NoOpOcrService`, no models loaded, byte-for-byte-unchanged deploy) plus per-org `Organisation.SelfHostedOcr` (additive migration `AddSelfHostedOcrFlag`). For a no-egress org the whole ingest/parse pipeline avoids OpenAI: PDFs route to the deterministic parser with scanned/image-only pages OCR'd by the self-hosted engine, and AI mapping (line-SKU suggestions + the magic auto-map field suggester) + email-body NLP + the AI schema-inference setup tool are all gated off → the no-egress guarantee is whole. Scanned lines stay review-flagged (no text layer to verify numbers against — assisted, not auto-delivered) and illegible scans still fail with the "scanned or image-only" message; both Dockerfiles add `libgomp1` + `libfontconfig1` (verified on `aspnet:8.0`). No-egress is an enterprise/operator config capability (not self-serve); the text/vision OpenAI PDF path is unchanged for non-no-egress orgs. Ships dormant by default.
- **Phase 4 ✅ SHIPPED 2026-06-05:** enrich the canonical model (supplier name, sub/tax/grand totals, payment terms, per-line line_amount/tax_rate/delivery_date) + PO-vs-invoice `document_type` classification. Records gained DEFAULTED params (zero blast radius); entities + DbContext + migration `AddOrderEnrichmentFields` add 9 NULLABLE columns; the strict schema + `ValidateAndMap` capture them; an `invoice` classification forces `pending_review` + a `ClassifiedAsInvoice` audit (an invoice on the PO path is never silently delivered as a PO). Full invoice-pipeline rerouting (creating an `InvoiceEntity` from a PO upload) remains a separate follow-up; UI surfacing of the new fields is a follow-up.

---

## Docs/copy to reconcile — do this AFTER the code works + tests pass (offer⇔works)
Update user-facing claims only once shipped. Full inventory:
- **Internal (safe to update with the decision):** `CLAUDE.md` Group F, `STATUS.md` (incl. the now-INVERTED L273 "do not treat the LLM as the OCR engine" — rewrite), `AGENTS.md` Group F, `docs/standards-matrix.md` (Azure rows + text-PDF "non-scanned only"), `docs/format-channel-roadmap.md` (Azure-DI-as-primary planning), `docs/integrations/ORDER_APIS.md` "OCR for scanned PDFs" section (rewrite — currently inverted, has `Ocr__Azure__*` env block), `docs/operator-onboarding-runbook.md` (scanned-PDF row), `docs/strategy/2026-06-03-live-readiness-brief.md`, `docs/product-selling-points.md` L78/L88-92, the `2026-06-01-boringly-reliable-po-loop.md` OCR section (mark superseded → maybe `docs/archive/`), `README.md`.
- **User-facing (gate on shipped+verified):** `formats/page.tsx:36-37` (scanned-PDF "document-AI provider" note), `catalog.ts` `pdf-text` conformance (founder's conservative SoT — keep tight), `UploadWorkbench.tsx:1324` (current OVERCLAIM: "per-zone confidence" — fix/soften), help pages `order-intake-options`/`first-upload`/`troubleshooting` (OCR/parse-time copy), `security/page.tsx:71` subprocessor row, `ParseFailureExplain.cs:12` `.pdf` string ("OCR isn't enabled" → new behavior).
- **Coupled tests that pin copy (change together):** `project-proculink/tests/e2e/live-po-failure-states.spec.ts:71` and the `ProcuLink.Api.Tests` theory asserting `"scanned or image-only"`.
- Fix the stale path in `docs/standards-matrix.md:42` (`ProcuLink.Core/Models/ParsedOrder.cs` → `ProcuLink.Transform/Parsing/ParsedOrder.cs`).

---

## Verification (must pass before marking done)
- `dotnet test ProcuLink.slnx` — all green (currently ~747). Add: extractor unit tests (fake `ChatClient` via the `InternalsVisibleTo("ProcuLink.Infrastructure.Tests")` ctor pattern — `OpenAiMappingService.cs:144-168`), a fallback-to-regex test, a validation/anti-hallucination test, an `OrderService` routing test.
- `dotnet build ProcuLink.slnx` clean (no Azure refs).
- Frontend `bun run build` clean (bun, never npm).
- Optional but recommended: re-run `~/pl_bench.py` against `~/Downloads/POs` to confirm extraction quality didn't regress.

## Guardrails
- Work on a **feature branch**; do NOT merge to `main` without founder OK.
- **Never commit secrets / API keys.** Extractor is no-op without a key (safe default).
- **Privacy:** real customer PO data → OpenAI requires an **EU-residency project + DPA + zero-retention**; note this in the docs and keep the no-egress (Phase 3) path on the roadmap.
- Keep backend tests green at every step; bun for frontend.
- This touches ≥3 files → it's a real feature: plan/execute carefully, `/code-review` at the end.

---

## Adversarial review (2026-06-05) — fixed vs deferred

Multi-agent review of the Phase 1 diff (correctness / security-tenancy / reuse lenses).

**Fixed in-branch (each with a regression test):**
- **HIGH** — space-joined numbers were merged as a thousands group (`"125 500"` → `125500`), so real lines tripped the anti-hallucination check → systematic false review flags (PdfPig joins words with single spaces). Removed the space from the grouped-thousands regex; space-separated numbers are now distinct.
- **MED** — genuine 3-decimal values (`"1.234"`) were read only as thousands → false flags. The source-number set now also carries the decimal reading of ambiguous 3-trailing-digit tokens.
- **MED** — unbounded LLM input could overshoot the per-org token cap in a single call. Source text is capped (`MaxSourceChars = 60k`) before the call.
- **LOW** — a duplicate model `line_number` over-flagged sibling lines. Lines are numbered positionally (`idx+1`) — unique, stable join keys for the review overlay + mapping.
- **LOW** — `double→decimal` overflow/NaN could throw from the pure `ValidateAndMap`. Guarded (`TryToDecimal`); malformed numbers flag the line instead of throwing.
- **LOW** — a `Guid.Empty` org id fell open on the cap. `ExtractAsync` now fails closed for a missing tenant before any OpenAI call.
- Added a snake_case JSON-binding unit test proving the `[JsonPropertyName]` DTO attributes work (no live call).

**Deferred (documented, not blocking Phase 1):**
- **MED** — anti-hallucination number-presence is matched document-wide, so a hallucinated value that coincides with another printed number can pass, and the arithmetic cross-check only runs when a line amount is stated. It's a defense-in-depth net, **not a correctness guarantee** (benchmark showed 0 hallucinations). Proximity-aware matching is a future enhancement.
- **MED** — PDF intake has no byte-size cap on the SFTP/S3/IMAP ingress channels, and PdfPig parsing isn't time/resource-bounded (a decompression-bomb PDF could DoS the worker). **Pre-existing** (the regex parser already calls PdfPig unbounded) and platform-level — track as ingress hardening, not specific to this feature.
- **MED** — customer PO text egresses to OpenAI gated only by key presence; there is no per-org enablement / data-residency enforcement in code. The spec already requires an EU-residency OpenAI project + DPA + zero-retention before enabling in prod; a per-org opt-in flag is a compliance follow-up. The no-key default is safe.
- **HIGH (separate file, out of scope)** — the sibling `OpenAiEmailBodyOrderExtractor` has the same snake_case→camelCase binding bug this extractor avoids (its DTOs lack `[JsonPropertyName]`, so email-body PO fields bind null under `JsonSerializerDefaults.Web`). Spun off as its own task.
