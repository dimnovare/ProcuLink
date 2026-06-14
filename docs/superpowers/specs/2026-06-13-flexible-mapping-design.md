# Flexible lossless income→outcome mapping — design (v2)

*Status: draft for review · 2026-06-13 · brainstormed against `main` · v2 folds in founder review*

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
grounding, per-supplier reuse, the Learn loop, replay/reproducibility). Usable by a
non-technical buyer AND a 30-year procurement veteran: one great experience, smart
defaults, power on demand.

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

1. **Keep canonical as the spine AND add a lossless raw bag** — income→outcome flows
   *through* the spine (standards, validation, catalog, reuse, Learn loop), with a bypass
   lane: any field — including raw-bag fields with no canonical slot — can wire straight to
   output. **AI suggests these direct mappings.**
2. **The canonical is EXTENSIBLE, not a fixed schema.** Three tiers (see Layer B): core
   typed spine (system) + **user-defined custom canonical fields (add/remove inline)** +
   lossless raw bag. The strict-JSON feel of today is replaced by a spine you can grow.
3. **Output target schemas come from MANY sources** (founder: "as many as possible"):
   standards templates (UBL / Peppol BIS / EDIFACT ORDERS / X12 850 / cXML), **infer from a
   sample** of the distributor's file, **import** the distributor's existing output, **clone**
   from another connection, hand-build field-by-field, or **AI-generate** from a description.
   Scriban template is the power escape hatch for shapes the field-map can't express.
4. **Author at the Supplier Connection, pin per order; per-order override is the exception
   lane.** Author the wire-map once, reuse for all that vendor's POs, pin `ConnectionRevisionId`
   for replay; tweak a weird doc per-order.
5. **Maximal lossless capture.** Structured formats: capture ALL source tokens (free, exact).
   PDF/email: widen the schema to recurring fields AND ask the LLM for a `raw_fields[]` array
   of everything else. Unverifiable LLM raw fields are review-flagged, never silently delivered.
6. **Catalog price = suggestion, never silent overwrite.** Surface "use catalog €X (was €Y,
   +Z%)" as an inline action. Add a connection-level **price-variance guard**: "if catalog vs
   PO price differs by > X%, HOLD the order for review" (configurable threshold, default off).

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

### Layer B — Storage: the three-tier extensible canonical

- **Tier 1 — Core spine (system, typed columns).** The recurring, standards-bearing,
  queryable fields. Promote to first-class:
  - New `OrderParty` table: `orderId, role` (enum: shipTo/billTo/remitTo/buyer/supplier),
    `name/street/city/postal/country/vat/regNr/ediCode/reference/contactName/email/phone`.
  - Per-line columns: `manufacturerPartNumber`, `customerPartNumber`, `discountPercent`,
    `unspsc`, `recipient`, `contractNumber`, `netAmount`.
  - Header columns: `incoterms`, `shippingMethod`, `buyerOrderRef`.
- **Tier 2 — Custom canonical fields (user-defined, add/remove).** A `CanonicalFieldDef`
  table scoped to org/connection: `key, label, scope` (header|line), `type`
  (string|number|date|bool), optional `standardsRef`, `order`. Values live in a typed-ish
  JSON column on the order/line keyed by `key`. The mapper's "+ Add field" writes a
  `CanonicalFieldDef`; removal soft-deletes (existing pinned revisions keep their copy).
  This is the "flexible canonical" — grow the spine without a migration per field.
- **Tier 3 — Lossless raw bag.** A dedicated `SourceCapture` table (`orderId, format,
  capturedAt, tokensJson` JSONB `{tokenId/xpath/label → value}`, `rawText`, `pageRefs`).
  **NOT `CanonicalJson`** — already triple-overloaded (enrichment snapshot + `buyerName`
  denorm + `mappingOverride`, `OrderMappingOverrideService.cs:9`). A separate row keeps the
  spine lean and makes the raw universe a first-class, version-pinnable, purge-surviving artifact.
- **Rule:** Tier 1 ⇒ recurring + standards + queryable. Tier 2 ⇒ user wants it on the spine
  (validatable, reusable, suggestable) but it's not universal. Tier 3 ⇒ document-specific
  long tail, "available to map but not promoted."

### Layer C — Mapping engine (source universe = canonical + raw bag + catalog)

- Expose `SourceCapture` tokens + Tier-2 custom fields as addressable `SourceMap` source
  tokens (already keyed by `cell:r#c#` / XPath — rule shape unchanged; engine reads the
  persisted token set instead of re-tokenizing a possibly-purged file).
- **AI mapping suggestions.** For each output field, the AI proposes a source (canonical,
  custom, or raw) with a confidence and reason — rendered as **ghost wires** the user
  accepts/rejects. Reuses the catalog allow-list guard discipline (no invented values). For
  direct source→output bypass (canonical lacks the field), the AI is the primary assistant.
- **Catalog accessor + price-variance guard.** A `catalog.*` Scriban accessor +
  `LoadCatalogProduct` manipulator: `{{ catalog.Price }}` / `{{ catalog.Code }}` looks up the
  resolved `supplierItemCode`/`manufacturerPartNumber`. Catalog price surfaces as a
  **suggestion** ("use catalog €X, PO has €Y, +Z%"), never silent. Connection-level
  `PriceVarianceGuard { enabled, thresholdPercent }`: if |catalog − PO| / PO > threshold,
  mark the line `NeedsReview` and HOLD the order.
- **Validation rules** on the spine: date sanity (DNV `06/12` flip), city ≠ label
  (REDACTED-PARTY "UIDNr"), qty×price reconcile, VAT format per country, required-field
  presence. Each rule marks `NeedsReview` + a reason; per-field delivery-blocking vs advisory.
- **Output: any shape already works** via `OutputMappingConfig` (field → arbitrary path) +
  `OutputTemplate` (whole-doc Scriban). Add: a **declared target schema** object (named
  output fields + types) the drag-UI wires into, populated from the **many sources** in
  Decision 3. Custom XML the fixed structured transformers can't express routes through
  template mode.

### Layer D — The unified three-pane mapper (UX is the product)

One component, used in BOTH the Supplier Connection editor (author once) and the inbox
per-order view (exception lane). Merges `PoMappingEditor` + `WireDragLayer` + `SpineConnectors`.
Built on the existing locked Bridge design tokens (violet/navy, shipped unified primitives,
shadcn/ui) — *not* a new visual language. Desktop-first power tool; mobile = read-only summary
+ approve/deliver.

**Layout.** Three resizable panes — **Incoming (source) │ Canonical spine │ Outgoing target**
— with an SVG wire layer over the top, a top action bar (Validate · Enrich · Manipulate ·
Preview · Deliver), and a collapsible live-preview pane (right or bottom).

**Field discovery — the anti-overwhelm pattern (the crux).** A raw bag can hold 100+ fields;
the user must still find what they need:
- **Grouped, collapsible source list:** `Header · Parties · Lines · Raw (N)`. Core groups
  expanded by default; **Raw bag collapsed by default** behind a "Raw fields (24)" expander
  (progressive disclosure — novices never see the firehose).
- **Debounced search** across labels AND values (you recognize a field by its content:
  `raw.recipient = c.walch@…`); autocomplete, "no results → try X" state.
- **Filter chips:** All / Unmapped / Mapped / AI-suggested / Has value.
- **Relevance-first ordering:** AI ranks raw fields by mapping-relevance to the current
  target; suggested ones pin to the top with a ghost-wire indicator.
- **Value preview inline** on every field so it's identifiable without opening the doc.
- **Virtualized** list for 50+ rows.

**Drag-wire interaction.** Drag from any source/canonical field handle to any output node;
**AI ghost wires** pre-drawn (dashed, faint, confidence %) — click ✓ accept / ✗ reject.
Snap zones; manipulator pills sit on a wire (the `fx`); **keyboard alternative** (select
source → Enter → select target) for a11y and power users. Drag has a movement threshold to
avoid accidental drags.

**Extensible canonical inline.** "+ Add field" at the bottom of the canonical pane → name +
type + optional standards ref → appears immediately as a wireable node (writes a
`CanonicalFieldDef`). Remove via the node's overflow menu (soft-delete).

**Inline badges.** Per field: teal "catalog" chip (enriched), green ✓ (validated), amber ⚠
(review, reason in tooltip), confidence ring on AI suggestions. Catalog price suggestion is an
inline one-click action.

**Live preview pane.** Debounced ~400ms; format toggle (XML/JSON/CSV/EDI/Scriban); the
just-touched field highlights; copy/download; "Deliver" disabled until validation is green.

**Progressive disclosure — 5-year-old AND 30-year veteran, one experience:**
- **Default (novice):** AI pre-maps everything; raw bag hidden; plain-language field names;
  one primary green CTA ("Looks right — deliver"); validation errors explained in words.
- **Power (veteran, always reachable, never a mode toggle):** Command palette (Cmd+K) —
  jump to field, add manipulator, switch output format, show standards mapping, edit Scriban;
  inline manipulator/Scriban editing; raw token ids; per-field standards references on demand;
  full keyboard wiring; column/JSON/EDI envelope view.

**States.** Empty (no source → "Upload a doc or pick a sample"), loading (skeleton lanes +
shimmer wires), no-search-results (suggest), extraction-failed (deterministic fallback +
manual map), AI-unavailable (no ghost wires; manual mapping still fully works). Deep-link the
URL to the open order/connection + selected field for sharing and state restoration.

### Layer E — Reproducibility

The mapping is authored on a **versioned Supplier Connection** and pinned per order via
`ConnectionRevisionId` (exists). `SourceCapture`, the Tier-2 `CanonicalFieldDef` set, and the
mapping must be **immutable + revision-pinned** and **outlive source-blob purge**
(`SourceFilePurgedAt`) so a replayed order re-maps against the same raw universe + field defs
and produces byte-identical output.

## Phased rollout (each independently shippable)

- **Phase 1 — Lossless capture + widened extraction (backend).** Widen extraction schema +
  `raw_fields`; persist full token set for structured formats; `OrderParty` table + line/header
  Tier-1 columns; `SourceCapture` table; round-trip (ingest→save→reload) tests on **real
  Postgres**; the 12-PO regression corpus. *First implementation plan.*
- **Phase 2 — Engine + extensible canonical + targets.** `CanonicalFieldDef` (Tier 2); raw
  tokens + custom fields as sources; `catalog.*` accessor + price-variance guard; validation
  rules; declared target schema from the many sources (standards/sample/import/clone/AI).
- **Phase 3 — The unified three-pane mapper + inbox redesign.** Merge the wire components;
  field-discovery (group/search/filter/virtualize/relevance); drag from raw→output; AI ghost
  wires accept/reject; inline add/remove canonical fields; manipulator pills; live preview;
  catalog + validation badges; command palette; progressive disclosure; states. Inbox "classic
  view" redesigned to host it (list → open → mapper → preview → deliver).
- **Phase 4 — Reproducibility + polish.** Pin `SourceCapture` + `CanonicalFieldDef` + mapping
  to revision; replay; retention beyond blob purge; standards visibility in the mapper.

## Risks

- **Locale / EU comma decimals.** Raw tokens carry locale strings (`1.234,56`). Any
  manipulator/Expression doing arithmetic on a raw token MUST parse with the locale heuristic
  (`;`-delimiter = EU hint, last-separator-is-decimal, read raw numeric cells) — else
  catalog-price variance + enrichment silently corrupt. No raw strings into numeric expressions
  un-parsed.
- **Column-vs-JSON split.** `BuyerName` lives in the column AND `CanonicalJson` (async parse
  updates only the column; `ScribanOrderModel:243` reads column-first). Every new Tier-1 field
  faces the same trap (EF-Ignored field + `ExecuteUpdateAsync` silently drops the value).
  Mandate the `RequestedDeliveryDate` pattern: real column, set at ingest, ingest→save→reload
  round-trip test on real Postgres.
- **Tier-2 custom-field sprawl.** Without governance, every org invents 50 fields. Scope defs
  to the connection, soft-delete, and keep AI suggestions biased toward Tier-1 first.
- **Validation blocking.** Widening adds review surface (scanned-PDF parties are unverifiable).
  Classify each field delivery-blocking vs advisory; don't let an unextractable ship-to block a
  clean PO.
- **Reproducibility.** `SourceCapture` + custom field defs + mapping must all be revision-pinned
  and survive blob purge, or replay diverges.
- **DocParser is beatable on accuracy too** (its own corpus errors: REDACTED-PARTY
  `ShipToCity="UIDNr. ATU"`, DNV date flip + `TotalVAT=1.00`, EXEMPLAR SEAFOOD city="NO - Norway", Danfoss
  merged contact, Chiesi PO-number prefix). Validation + catalog grounding should catch these.

## Test corpus (regression suite)

12 real vendor POs (PDF + DocParser reference): REDACTED-PARTY (DE/AT, XFA/SAP), EXEMPLAR SEAFOOD (NO,
per-line recipient), LähiTapiola (FI, EDI id, discount), REDACTED-PARTY (FR, Incoterms), Siemens
(PL, reg-no), Chiesi (IT), REDACTED-PARTY (PL, contract no), DNV (PL, recipient, MM/DD date), Danfoss
(DK, split contact), REDACTED-PARTY (DK, UNSPSC), Rheinbahn (DE, contract), Gjensidige (NO, EDI code).
8 countries, 4 currencies (EUR/NOK/PLN/DKK), single + multi-line, 1–3 pages.

## Non-goals

- Not rebuilding the canonical model or the override/transform engine (reuse them).
- Not replacing Scriban (it stays the power escape hatch).
- Not a self-serve "advanced mode" toggle (one great experience; progressive disclosure).
- Not a new visual language — reuse the locked Bridge design tokens + shipped primitives.

## Resolved (was open)

- Target schemas seed from standards + sample + import + clone + AI-generate + hand-built.
- Catalog price is a suggestion + a configurable variance-hold threshold (not auto-apply).
- The canonical is extensible (Tier-2 custom fields), not a fixed schema.
