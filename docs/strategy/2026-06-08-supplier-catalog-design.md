# Supplier Product Catalog — grounding AI code suggestions in reality

**Date:** 2026-06-08
**Status:** Design (read-only investigation; no code changed)
**Answers the founder's #1 open question:** *"How does the AI know THE CORRECT
supplier product code to suggest? We need data from the supplier/distributor on
what values are even possible."*

---

## 1. The problem, in the actual code

Today, when a buyer line can't be resolved deterministically, the AI is asked to
**invent** a supplier item code from two weak signals only:

1. **Past resolutions** (`item_mappings`) — the only "evidence" the prompt sees.
   `OrderIngestionService.GetAiMappingCandidatesAsync`
   (`ProcuLink.Api/Services/Orders/OrderIngestionService.cs:881-896`) pulls **at
   most 40** prior `buyerCode → supplierCode` rows for that (org, supplier) and
   passes them as `AiMappingCandidate(BuyerItemCode, SupplierItemCode, "existing
   mapping X -> Y")`.
2. **The buyer's own line** (`buyerItemCode`, `description`, `qty`, `unit`) —
   `AiMappingLineContext` (`ProcuLink.Core/Services/Ai/IAiMappingService.cs:59-64`).

The model then free-forms a `supplierItemCode` string
(`OpenAiMappingService.SuggestSupplierItemCodesAsync` /
`SuggestChunkAsync`, `OpenAiMappingService.cs:356-551`). The JSON schema
(`BatchSuggestionSchema`, lines 50-89) constrains the *shape* but **not the
value** — `supplierItemCode` is an unconstrained `string`. The system prompt even
says *"Use existing candidate mappings when they support the suggestion"* — but if
none do, **nothing stops the model emitting a plausible-looking code that does not
exist in the supplier's catalog.** The line-SKU suggester has **no `allowedColumns`
guard** like the field-mapping suggester does (contrast `OpenAiMappingService.cs:665-670`,
which *does* reject any column the model wasn't given). For the **first order from a
new supplier**, `item_mappings` is empty, so there is *zero* grounding — pure
hallucination risk.

The same weakness flows to the UI. The new datalist typeahead in
`SpineReview.tsx` (`knownSupplierCodes`, lines 1794-1811; `ManualCodeRow`, lines
443-470) is sourced from `apiClient.getSupplierMappings(supplierId)` — i.e.
**again only past `item_mappings`**. A code typed manually that has never been
mapped before gets no validation at all (`novel` flag at line 449 only styles it,
doesn't block it).

**Root cause:** ProcuLink has never held the supplier's ground truth — *the set
of codes that actually exist*. `item_mappings` is a learned cache of resolutions,
not a catalog. There is no `products` / `catalog` entity anywhere in the schema
(`ProcuLinkDbContext.cs:13-52` — confirmed: no product/catalog DbSet).

> **One-line framing for the founder:** the AI is a *matcher*, not an *oracle*.
> It can only pick the right code if we first tell it which codes are real. The
> Supplier Product Catalog is that list of real codes.

---

## 2. Solution overview

Introduce a **supplier-scoped Product Catalog**: the authoritative list of
products a given supplier/distributor can actually fulfil. It becomes:

- the **retrieval corpus** the AI matches against (suggest only REAL codes, with
  the matched catalog row as evidence);
- the **source of truth** for the datalist typeahead (full catalog, not just past
  mappings);
- the basis for a **"code not in catalog" validation warning** on manual entry;
- the per-supplier **ground-truth layer** beneath the future cross-org library
  (catalog = facts; cross-org = hints).

Design principle (founder's **offer ⇔ works** rule): the catalog is **optional**.
When a supplier has no catalog, behaviour is byte-for-byte today's (mappings +
description signals). The catalog only ever *tightens* correctness — it never
becomes a hard prerequisite for using the product.

---

## 3. Data model

New entity `SupplierProduct` (table `supplier_products`), mirroring the tenancy
and EF-config conventions of `ItemMapping`
(`ProcuLinkDbContext.cs:374-398`):

```
SupplierProduct
  Id                Guid      (pk)
  OrgId             Guid      (every query filters on this — never cross-org)
  SupplierId        Guid
  SupplierItemCode  string    REQUIRED   — the REAL code (the thing the AI may suggest)
  Name             string?    (product/description as the supplier calls it)
  Unit             string?    (PCS/M/KG…)
  UnitPrice        decimal?   (optional list price; advisory only)
  Currency         string?    (ISO-4217, optional, pairs with UnitPrice)
  Barcode          string?    (GTIN/EAN/UPC — strong exact-match key when buyers carry it)
  Aliases          string?    (jsonb: array of alt codes/old SKUs/manufacturer codes)
  IsActive         bool       (default true; discontinued products stay for audit, excluded from suggest)
  Source           string     (manual | csv | xlsx | erp_erply | erp_directo | onboarding)
  ExternalId       string?    (ERP product id, for idempotent re-sync)
  NormalizedName   string?    (computed: lower+trim+collapse-ws+strip-punct — match key)
  SearchTokens     string?    (computed token set for cheap retrieval; see §5)
  CreatedAt        DateTime
  UpdatedAt        DateTime
  LastSyncedAt     DateTime?  (last ERP sync that touched this row)
```

**Indexes / constraints (mirror `ItemMapping.cs:388-390`):**
- Unique `(OrgId, SupplierId, SupplierItemCode)` — one row per real code.
- Index `(OrgId, SupplierId, IsActive)` — the suggest/typeahead read path.
- Index `(OrgId, SupplierId, Barcode)` — exact GTIN resolution.
- `(OrgId, SupplierId, NormalizedName)` — name retrieval.

**Why a new entity and not extend `item_mappings`:** they are different facts.
`item_mappings` = *"buyer X means supplier Y (we learned this)."*
`supplier_products` = *"supplier Y exists (this is ground truth)."* Keeping them
separate lets the catalog validate the mappings (a mapping whose
`SupplierItemCode` is absent from an otherwise-populated catalog is itself
suspect and can be flagged). Navigation: add
`List<SupplierProduct> Products` to `Supplier` (`Supplier.cs:19-25`).

Optional companion `SupplierCatalogImport` (table `supplier_catalog_imports`) for
audit/idempotency of bulk loads: `Id, OrgId, SupplierId, Source, RowCount,
CreatedCount, UpdatedCount, DeactivatedCount, FileName, CreatedAt`. Lets the UI
show "last synced 2h ago, 1,240 products" and supports ERP delta runs.

---

## 4. Import sources

All write paths converge on one idempotent `ISupplierCatalogService.UpsertManyAsync(orgId,
supplierId, IEnumerable<SupplierProductDraft>, source, ct)` keyed on
`(SupplierItemCode)` (or `ExternalId` for ERP). Upsert mirrors
`ItemMappingService.UpsertAsync` (`ItemMappingService.cs:80-140`): existing →
update fields + `UpdatedAt`; absent → insert.

### 4a. CSV / XLSX upload — universal, ships first
Reuse the exact pattern already proven for mapping import:
`SuppliersController.ImportMappings` (`SuppliersController.cs:398-448`) + the
Transform layer's `CsvOrderParser`/XLSX reader (ClosedXML — note: openpyxl-xlsx
fails on prod .NET per the "live format testing" learning, so use ClosedXML).
New endpoint `POST /api/suppliers/{id}/catalog/import` (CSV/XLSX). Column
auto-detection can reuse the **existing magic mapping field suggester**
(`AiAugmentedFieldMappingSuggester` + `IAiMappingService.SuggestFieldMappingsAsync`)
to map arbitrary supplier-export columns → `{code, name, unit, price, barcode}`.
This is the universal path — every supplier/distributor can export a product list.

### 4b. ERP sync via the EXISTING Erply / Directo connectors
**Investigation result: today's connectors are DELIVERY-ONLY and cannot read a
catalog.** `IErpConnector` (`IErpConnector.cs:5-22`) exposes a single
`SendAsync(ErpDeliveryRequest)` — outbound push of a generated artifact. Neither
`ErplyConnector` (`ErplyConnector.cs:29-79`) nor `DirectoConnector`
(`DirectoConnector.cs:27-82`) has any product/list/GET method; both only POST.
So ERP catalog sync is **net-new pull capability**, but it maps cleanly onto the
real upstream APIs:

- **Erply** has a JSON-RPC product API (`getProducts`, paged, returns
  `productID, code, code2 (EAN/UPC), name, price, unitName, active`). This is the
  catalog. Add `IErpCatalogConnector.FetchProductsAsync(config, creds, ct)` →
  `SupplierProductDraft[]`; `code` → `SupplierItemCode`, `code2` → `Barcode`,
  `productID` → `ExternalId`.
- **Directo** exposes item data via its XML report/`xmlcore` API
  (`item`/`artikkel` lists with `code, name, unit, price, EAN`). Same mapping into
  `SupplierProductDraft`.

Reuse the connectors' existing config/credential plumbing: encrypted
`SupplierDeliveryConfig` already stores ERP URL + AES-GCM creds and is decrypted
into `request.DecryptedCredentials` (`IErpConnector.cs:12-17`, consumed at
`ErplyConnector.cs:54` / `DirectoConnector.cs:38-52`). A new `CatalogSyncJob`
(Hangfire, recurring, idempotent — same family as the existing Hangfire jobs,
keyed on `ExternalId`, must obey the "await, never fire-and-forget" learning and
not share a scoped DbContext) pulls products on a schedule and upserts. Honest
scope note (offer ⇔ works): ship the **Erply** puller first (richest API, primary
ICP), Directo second; gate the ERP-sync UI behind real test-fire before claiming
it. SSRF: route the outbound fetch through the existing `OutboundRequestGuard`.

### 4c. Supplier onboarding
When a buyer adds a supplier, the supplier-setup flow offers three grounding
options in priority order: **(1) connect ERP** (4b, best — live truth), **(2)
upload a product export** (4a, universal), **(3) skip** (degrade to today's
mappings-only behaviour). This is the moment to capture ground truth, mirroring
how the PO-mapping editor already offers "Apply starter template". A future
supplier-facing share link (supplier uploads their own catalog without buyer
involvement) is a natural extension but **out of scope for v1**.

---

## 5. How it grounds the AI suggestion (the core of the answer)

Replace "candidates = last 40 mappings" with **catalog-grounded retrieval**, in
`OrderIngestionService.GetAiMappingCandidatesAsync` /
`BuildLineEntitiesAsync` (`OrderIngestionService.cs:780-896`):

**Pass 0 — exact, no AI:** if a line carries a barcode/GTIN, or its
`buyerItemCode`/`description` exactly equals a catalog `SupplierItemCode` /
`Barcode` / alias → resolve directly to that real code, `Confidence = 1.0`, source
`"catalog-exact"`. Cheapest, safest, no token spend.

**Pass 1 — deterministic mappings:** unchanged (`ResolveManyAsync`,
`ItemMappingService.cs:39-77`).

**Pass 2 — catalog-grounded retrieval → AI re-rank (for leftovers):**
1. **Retrieve** the top-K (≈20-40) catalog rows most similar to the buyer line.
   v1 retrieval is **lexical and dependency-free** (Postgres is plain Npgsql; **no
   pgvector** — confirmed, no vector/embedding infra exists in the repo): normalize
   the buyer description (`NormalizedName`/`SearchTokens` columns), score by
   token-overlap / trigram similarity (pg_trgm) / prefix match against catalog
   `NormalizedName` + `SupplierItemCode`. This runs in one org+supplier-scoped query.
2. **Constrain the model to reality.** Pass those K real rows as the candidate set
   (extend `AiMappingCandidate` to carry `Name/Unit/Price/Barcode`), and **add the
   missing allow-list guard**: after the model responds, **reject any
   `supplierItemCode` not present in the retrieved catalog set** — exactly the guard
   the field-mapper already has (`OpenAiMappingService.cs:665-670`) but the
   line-SKU path lacks. Tighten the JSON schema/prompt so the model **selects** a
   `catalogProductId` from the provided list rather than free-typing a string.
   Result: **the AI can never suggest a code that isn't in the catalog.**
3. **Evidence = the matched catalog row.** `AiMappingSuggestion.Provenance`
   becomes concrete: *"Matched catalog product ES-RES-220R 'Resistor 220Ω 0.25W'
   (price €0.04, unit PCS) — 91% name similarity."* This is exactly the
   trustworthy, veteran-grade provenance the standards-visibility rule wants, and
   it replaces today's vague "Buyer description evidence" string.

**Phase 2 (optional, later): semantic retrieval.** If lexical recall proves
insufficient on messy descriptions, add embeddings (OpenAI `text-embedding-3-small`
or self-hosted for no-egress) stored in a `supplier_product_embeddings` table +
pgvector. Same contract; only retrieval improves. **No-egress orgs**
(`Organisation.SelfHostedOcr`, the existing chokepoint at
`OpenAiMappingService.cs:196-217` and `OrderIngestionService.cs:815-819`) skip the
embedding/AI re-rank entirely and use **lexical catalog retrieval only** — which is
*better* than today (real codes, on-prem) with zero egress. The catalog makes the
no-egress path stronger, not weaker.

**Net effect:** first order from a brand-new supplier goes from *"no grounding,
free-form hallucination"* to *"suggest the closest real product, or honestly say
'no catalog match — needs review.'"*

---

## 6. Powering the typeahead + "not in catalog" warning

- **Datalist typeahead.** Point `SpineReview`'s `knownSupplierCodes`
  (`SpineReview.tsx:1794-1811`) at a new `GET /api/suppliers/{id}/catalog` (active
  rows, code + name) instead of `getSupplierMappings`. Now the dropdown shows the
  **full real product list with names** (e.g. `ES-RES-220R — Resistor 220Ω`), not
  just the handful of previously-mapped codes. Keep mappings as a secondary source
  so historical codes still appear even if not yet in the catalog.
- **"Code not in catalog" validation.** `ManualCodeRow` already computes a `novel`
  flag (`SpineReview.tsx:449`) but only uses it for styling. Upgrade it: when the
  supplier **has** a catalog and the typed code isn't in it, show an inline amber
  warning *"This code isn't in {supplier}'s catalog — double-check it exists"* with
  a "use anyway" affordance (never a hard block — buyer may legitimately know a new
  code; offer ⇔ works means warn, don't lie). When the supplier has **no** catalog,
  no warning (nothing to validate against) — identical to today.
- Server mirror: `resolve`/`commitMappings` can optionally annotate the saved
  mapping with `catalogMatched: bool` for the exception dashboard
  (`/operations/exceptions`) to surface "delivered with an unverified code."

---

## 7. Tenancy & privacy

- **Strictly org-scoped**, every query `Where(OrgId == organisationId &&
  SupplierId == ...)` — same invariant as `ItemMappingService` and the CLAUDE.md
  "EF queries without org_id scope — ever" rule. The catalog is the **buyer org's**
  copy of *their* supplier's product list; it is never shared across tenant
  boundaries without explicit, separate opt-in (that's the cross-org library, §8).
- **No-egress respected:** catalog data of a `SelfHostedOcr` org never goes to
  OpenAI; retrieval is lexical/local; embeddings (if added) are self-hosted. The
  catalog *extends* the existing single chokepoint, it doesn't create a new leak.
- **Credential reuse:** ERP sync uses the existing AES-GCM-encrypted
  `SupplierDeliveryConfig`; no new secret store. SSRF via `OutboundRequestGuard`.
- **Price sensitivity:** `UnitPrice` is optional and advisory; treat as
  confidential supplier data, never expose cross-org, never send to the LLM unless
  it adds matching value (it usually doesn't — name + code do the work).

---

## 8. Relationship to the cross-org mapping library

These are **two layers, not competitors**:

- **Supplier Product Catalog (this design) = per-supplier GROUND TRUTH.**
  Authoritative, owned by one buyer org, the set of codes that provably exist.
  Constrains suggestions to reality. Ships now; wins customer #1.
- **Cross-org mapping library (Horizon 3, Group Q — explicitly future, see
  `SchemaFingerprint.cs:10-11` which scopes cross-org *out*) = aggregated HINTS.**
  Anonymized, opt-in: "across all orgs, buyers describing 'M10x50 steel bolt' map
  to supplier code patterns like X." A *prior* for retrieval/ranking — never a
  source of truth, because another org's mapping may be wrong for *this* supplier.

The catalog is the safe, sellable first step; it also makes the cross-org library
*safe later* — cross-org hints get filtered through each org's own catalog
(suggest a hint only if that real code exists in this supplier's catalog),
eliminating the obvious cross-org-pollution risk. **Catalog first, library later.**

---

## 9. Phasing

- **P1 — Catalog model + CSV/XLSX import + typeahead/warning (highest ROI, no AI
  change).** `SupplierProduct` entity + EF config + migration; `ISupplierCatalogService`;
  `POST/GET /api/suppliers/{id}/catalog` (+ `/import` reusing
  `SuppliersController.cs:398-448`); repoint `SpineReview` datalist + add the
  not-in-catalog warning. Ships real grounding for manual entry immediately.
- **P2 — Catalog-grounded AI suggestion.** Pass-0 exact match; lexical retrieval
  (pg_trgm) in `GetAiMappingCandidatesAsync`; extend `AiMappingCandidate`;
  **add the allow-list guard** so the model can never emit a non-catalog code;
  concrete catalog-row provenance.
- **P3 — Erply ERP catalog pull** (`IErpCatalogConnector.FetchProductsAsync` +
  recurring idempotent `CatalogSyncJob`), then **Directo**. Onboarding "connect
  ERP / upload export / skip" step.
- **P4 (optional) — semantic retrieval** (embeddings + pgvector) for messy
  descriptions; self-hosted variant for no-egress; cross-org hint layer filtered
  through each org's catalog.

Throughout: catalog is **optional** — absent ⇒ exactly today's behaviour
(offer ⇔ works). All Hangfire work idempotent; awaited (never fire-and-forget on a
shared scoped DbContext).
