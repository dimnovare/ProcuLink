# Flexible lossless income→outcome mapping — design

*Status: draft for review · 2026-06-13 · brainstormed against `main`*

## Problem

ProcuLink's pipeline is `inbound → canonical → outbound`. The canonical model is a
fixed set of typed slots. Any field with no slot is dropped *before storage is even
considered* — so it can never be mapped or delivered. A real customer PO (REDACTED-PARTY,
EXEMPLAR SEAFOOD, LähiTapiola, REDACTED-PARTY, Siemens, Chiesi, REDACTED-PARTY, DNV, Danfoss, REDACTED-PARTY, Rheinbahn,
Gjensidige — 12 real vendors, 8 countries, 4 currencies) carries 30+ fields; canonical
keeps ~12. DocParser captured the rest (ship-to, bill-to, VAT/EDI ids, contact,
manufacturer part number, Incoterms, discounts, per-line recipient). We threw them away.

The founder's goal: **be strictly better than DocParser / Altova MapForce.** Take ANY
inbound doc, capture EVERYTHING losslessly, validate it, enrich it from the catalog
(correct price/code), and map incoming → outgoing with drag-wires to ANY output
shape/format — while keeping our edge (standards visibility, validation, catalog
grounding, per-supplier reuse, the Learn loop, replay/reproducibility).

## Verdict on "the canonical model is a lossy funnel"

True — but the leak is **three serial narrowings**, and the first is the real culprit:

1. **Extraction schema** (`OpenAiPdfOrderExtractor.cs:54-92`) — strict JSON,
   `additionalProperties:false` at every level. No `ship_to`/`bill_to`/`vat`/`contact`/
   `manufacturer_part_number`/`incoterms`. The model is *structurally forbidden* from
   emitting them. **Fields die here, before storage.**
2. **Entity columns** (`PurchaseOrderEntity.cs:11-118`) — no column for any
   address/contact/VAT/MPN. Second wall.
3. **Output model** (`ScribanOrderModel.cs:167-219`) — already *reads* a
   `shipTo`/`deliveryAddress` from `CanonicalJson` if present. A read path with **no
   writer**. The output side is already less lossy than the input.

So the fix starts at extraction, not the canonical entity.

## What already exists (build on, do NOT rebuild)

~70% of the target is shipped and live:

- **Per-order override engine** (`OrderMappingOverride.cs`): `CustomFields` (lossless
  hand-add), `SourceMap` (re-derive any field from source tokens, Expression > token >
  fixed > passthrough + manipulators), `OutputMappingConfig` (canonical → arbitrary
  output path), `OutputTemplate` (whole-document Scriban → any shape).
- **8 manipulators** via `ManipulatorRegistry` (Replace/Trim/DateFormat/Concat/Fallback/
  Split/Multiply/Divide), reused across SourceMap, Output, supplier mapping.
- **6-mode transform dispatch** (`OrderTransformService`): template > native override >
  structured override > pinned-revision > supplier > fixed; output format resolves from
  a pinned `ConnectionRevisionId` (`PurchaseOrderEntity.cs:118`) — reproducibility primitive in place.
- **Wire UI exists twice**: `PoMappingEditor.tsx` (source-column → canonical, SVG bezier,
  confidence, accept/edit/reject; target = hard-coded 10-field `ALL_CANONICAL:71-82`) and
  **`WireDragLayer.tsx`** which **already does drag-to-connect** in the inbox
  (canonical → output, pointer events, snap zones, persists to `OrderMappingOverride.output`).
- **`OutputMappingEditor.tsx`**: field-mode (source + manipulator chain) AND template-mode
  (Scriban + ~400ms live preview), persists via `PUT /mapping-override`.
- **Catalog grounding**: `SupplierProduct` (Code/Name/Unit/Price/Barcode/IsActive),
  trigram + exact-code/barcode retrieval, AI allow-list guard; catalog price/unit/barcode
  already flow into the Scriban model.
- **`mapping-override/preview`** endpoint + live preview.

**This is a widen + unify job, not a greenfield build.**

## Decisions (forks resolved with the founder)

1. **Keep canonical as the spine AND add a lossless raw bag.** Income→outcome flows
   *through* the spine (standards, validation, catalog, reuse, Learn loop), with a bypass
   lane: any field — including raw-bag fields with no canonical slot — can wire straight to
   output. Best of both; not a canonical rebuild, not pure passthrough.
2. **Output target: offer all paths.** Declared target schema (drag into named fields) as
   the comfortable default; **infer target schema from a sample** of the distributor's file
   to bootstrap it; Scriban template as the power escape hatch. Validate + manipulate
   everywhere.
3. **Author at the Supplier Connection, pin per order; per-order override stays as the
   exception lane.** A distributor sends ~500 POs/mo to one vendor — author the wire-map
   once, reuse, pin `ConnectionRevisionId` for replay; tweak a weird doc per-order.
4. **Maximal lossless capture.** Structured formats: capture ALL source tokens (free,
   exact). PDF/email: widen the schema to recurring fields AND ask the LLM for a
   `raw_fields[]` array of everything else. Unverifiable LLM raw fields are review-flagged,
   never silently delivered.

## Architecture

### Layer A — Extraction (widen + raw bag)

- Widen the strict schema (`OpenAiPdfOrderExtractor.cs:54-92`, `SystemPrompt:96-120`,
  `ExtractionDto/ExtractionLineDto:754-777`): add a `parties` object (`ship_to`,
  `bill_to`, `remit_to`: name/street/city/postal/country/vat/reference), header `contact`
  (name/phone/email), header `incoterms`/`shipping_method`/`payment_terms`/`buyer_order_ref`,
  and per-line `manufacturer_part_number`, `customer_part_number`, `discount_percent`,
  `unspsc`, `recipient`, `contract_number`, `net_amount`.
- Add `raw_fields: [{label, value, group}]` (header + per-line) for everything not
  modelled. Cap count; review-flag (the verbatim-number anti-hallucination check only
  covers numbers present in source text).
- Widen the email extractor to parity.
- **Structured formats (CSV/XLSX/XML): the lossless bag is free** — the tokenizer already
  produces `SourceToken{Id,Label,Value,Group}` and discards unmapped ones today. **Persist
  the full token set** instead of dropping it. No LLM, no hallucination.

### Layer B — Storage (promote recurring, bag the long tail)

- **Promote recurring, standards-bearing, queryable fields to first-class
  columns/tables** (per north-star "stop overloading `CanonicalJson`"):
  - New `OrderParty` table: `orderId, role` (enum: shipTo/billTo/remitTo/buyer/supplier),
    `name/street/city/postal/country/vat/regNr/ediCode/reference/contactName/email/phone`.
  - Per-line columns: `manufacturerPartNumber`, `customerPartNumber`, `discountPercent`,
    `unspsc`, `recipient`, `contractNumber`, `netAmount`.
  - Header columns: `incoterms`, `shippingMethod`, `buyerOrderRef`.
- **Lossless long tail → a dedicated `SourceCapture` table** (`orderId, format,
  capturedAt, tokensJson` (JSONB: `{tokenId/xpath/label → value}`), `rawText`, `pageRefs`).
  **NOT `CanonicalJson`** — which already holds enrichment snapshot, `buyerName` denorm,
  AND `mappingOverride` (`OrderMappingOverrideService.cs:9`); adding the full document would
  bloat every order row and entangle the override sub-document.
- **Rule:** typed column ⇒ recurring + standards-mapped + queryable. `SourceCapture` JSONB
  ⇒ document-specific long tail, "available to map but not promoted."

### Layer C — Mapping engine (source universe = canonical + raw bag + catalog)

- Expose `SourceCapture` tokens as addressable `SourceMap` source tokens (already keyed by
  `cell:r#c#` / XPath — rule shape unchanged; engine reads the persisted token set instead
  of re-tokenizing a possibly-purged file).
- Add a `catalog.*` accessor to the Scriban scope + a `LoadCatalogProduct` manipulator:
  `{{ catalog.Price }}` / `{{ catalog.Code }}` looks up the resolved
  `supplierItemCode`/`manufacturerPartNumber` and emits the catalog's price/unit/barcode —
  closing "correct price/code." Catalog data is already in the model; this adds the
  default-path accessor.
- **Validation rules** on the spine: date sanity (DNV `06/12` flip), city ≠ label
  (REDACTED-PARTY "UIDNr"), qty×price reconcile, VAT format per country, required-field
  presence. Each rule marks `NeedsReview` + a reason; decide per-field whether
  delivery-blocking or advisory (don't let an unextractable ship-to block a clean PO).
- **Output: any shape already works** via `OutputMappingConfig` (field → arbitrary path) +
  `OutputTemplate` (whole-doc Scriban). Add: a **declared target schema** object (named
  output fields + types) the drag-UI wires into and validates against, and **infer a target
  schema from an uploaded sample** of the distributor's file. Custom XML that the fixed
  structured transformers can't express routes through template mode.

### Layer D — Unified three-lane drag mapper UI

- Merge `PoMappingEditor` + `WireDragLayer` + `SpineConnectors` into ONE component: three
  columns — **raw source │ canonical spine │ declared output target** — source list and
  target list are props.
- Left lane = canonical fields + `SourceCapture` raw tokens (the lossless universe). Drag a
  wire from any left/spine node to any output node; raw-bag fields can bypass the spine
  straight to output.
- Right lane = the declared target schema (or inferred-from-sample). On drop, write an
  `OutputFieldRule{outputPath}` (the type already exists).
- Manipulator pills sit on wires (the `fx`); inline validation flags on spine fields;
  catalog badges where enrichment applies; live output preview pane (reuse
  `mapping-override/preview`).
- **Two homes, one component:** the Supplier Connection editor (author once, reused) and
  the inbox per-order view (the exception lane). The inbox "classic view" is redesigned to
  host this: list → open order → three-lane mapper → preview → deliver.

### Layer E — Reproducibility

- The mapping is authored on a **versioned Supplier Connection** and pinned per order via
  `ConnectionRevisionId` (exists). `SourceCapture` must be **immutable and revision-pinned**
  and **outlive source-blob purge** (`SourceFilePurgedAt`) so a replayed order re-maps
  against the same raw universe and produces byte-identical output.

## Phased rollout (each independently shippable)

- **Phase 1 — Lossless capture + widened extraction (backend).** Widen extraction schema +
  `raw_fields`; persist full token set for structured formats; `OrderParty` table + line/header
  columns; `SourceCapture` table; round-trip (ingest→save→reload) tests on **real Postgres**;
  the 12-PO regression corpus. *First implementation plan.*
- **Phase 2 — Engine: source universe + catalog accessor + validation + target schema.**
  Raw tokens as sources; `catalog.*` accessor; validation rules; declared target schema +
  infer-from-sample.
- **Phase 3 — Unified three-lane drag mapper + inbox redesign.** Merge the wire components;
  drag from raw→output; manipulator pills; live preview; inline flags; author on Connection
  + per-order override; redesigned inbox.
- **Phase 4 — Reproducibility + polish.** Pin `SourceCapture` + mapping to revision; replay;
  retention beyond blob purge; standards visibility in the mapper.

## Risks

- **Locale / EU comma decimals.** Raw tokens carry locale strings (`1.234,56`). Any
  manipulator/Expression doing arithmetic on a raw token MUST parse with the locale
  heuristic (`;`-delimiter = EU hint, last-separator-is-decimal, read raw numeric cells) —
  else catalog-price enrichment silently corrupts. Do not let raw strings enter numeric
  expressions un-parsed.
- **Column-vs-JSON split.** `BuyerName` lives in the column AND `CanonicalJson`, written by
  different paths (async parse updates only the column); `ScribanOrderModel:243` reads
  column-first. Every new promoted field (parties, VAT, MPN) faces the same trap (EF-Ignored
  field + `ExecuteUpdateAsync` silently drops the value). Mandate the `RequestedDeliveryDate`
  pattern: real column, set at ingest, ingest→save→reload round-trip test on real Postgres.
- **Validation blocking.** Widening adds review surface (scanned-PDF parties are
  unverifiable). Classify each new field delivery-blocking vs advisory.
- **Reproducibility.** `SourceCapture` + mapping must both be revision-pinned and survive
  blob purge, or replay diverges.
- **DocParser is beatable on accuracy too** (its own errors in the corpus: REDACTED-PARTY
  `ShipToCity="UIDNr. ATU"`, DNV date flip + `TotalVAT=1.00`, EXEMPLAR SEAFOOD city="NO - Norway",
  Danfoss merged contact, Chiesi PO-number prefix). Validation + catalog grounding should
  catch these — a marketing + correctness edge.

## Test corpus (regression suite)

12 real vendor POs (PDF + DocParser reference): REDACTED-PARTY (DE/AT, XFA/SAP), EXEMPLAR SEAFOOD (NO,
multi-line, per-line recipient), LähiTapiola (FI, EDI id, discount), REDACTED-PARTY (FR, Incoterms,
multi-line), Siemens (PL, reg-no, long line desc), Chiesi (IT), REDACTED-PARTY (PL, contract no),
DNV (PL, recipient, MM/DD date), Danfoss (DK, split contact), REDACTED-PARTY (DK, UNSPSC), Rheinbahn
(DE, contract), Gjensidige (NO, EDI code). Covers 8 countries, 4 currencies (EUR/NOK/PLN/DKK),
single + multi-line, 1–3 pages.

## Non-goals

- Not rebuilding the canonical model or the override/transform engine (reuse them).
- Not replacing Scriban (it stays the power escape hatch).
- Not a self-serve "advanced mode" toggle (one great experience; progressive disclosure).

## Open questions

- Should the declared target schema be importable from standards (UBL/Peppol/X12 templates)
  as starting points, or only sample-inferred / hand-built?
- Catalog enrichment default: auto-apply catalog price when it differs from the PO price, or
  always surface as a suggestion the operator confirms?
- Raw-bag retention window vs storage cost for high-volume orgs.
