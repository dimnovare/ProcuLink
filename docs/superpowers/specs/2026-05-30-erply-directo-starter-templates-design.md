# Erply & Directo Starter PO Mapping Templates — Design

**Date:** 2026-05-30 · **Status:** Approved (design), ready for implementation
**Why:** the #1 sales-leverage item in the investor analysis — pre-built mapping templates so a
new Erply/Directo customer's first PO delivery takes minutes, not a from-scratch mapping build.

## Goal
Ship pre-built `PoMappingConfig` starter templates for **Erply** and **Directo** that a user
*applies* in `PoMappingEditor` instead of hand-mapping every column. "JSON config, zero code" to
add more ERPs later.

## Approach (chosen)
**Static JSON template fixtures** (global, not per-org) → a **read-only list endpoint** → an
**"apply" affordance in the existing editor** that loads a template's config client-side; the user
reviews and saves via the **existing** PUT (`IPoMappingService.UpsertAsync`). No DB changes, no new
mapping engine, reuses the save path.

*Rejected:* a seeded `PoMappingTemplate` DB table — adds a migration + admin surface for no
pilot-stage benefit.

## Canonical fields (authoritative — from `PoMappingEngine.Apply`)
- Header: `PoNumber`, `OrderDate`, `BuyerName`, `Currency`
- Lines: `LineNumber`, `BuyerItemCode`, `Description`, `Quantity`, `Unit`, `UnitPrice`

## Templates
Each fixture is a `PoMappingConfig`:
`{ HasHeaderRecord, Separator, Header: {canonical → {ExternalField | FixedValue, FieldManipulators[]}}, Lines: {…} }`.

### Erply (source: `getPurchaseDocuments` API → CSV export)
- Header: `PoNumber`←`number`, `OrderDate`←`date` (+ `DateFormat` manipulator), `BuyerName`←`clientName`, `Currency`←`currencyCode`
- Lines: `BuyerItemCode`←`code`, `Description`←`itemName`, `Quantity`←`amount`, `Unit`←`unitName`, `UnitPrice`←`price`; `LineNumber` omitted (optional — no line-number column in either export)

### Directo (source: `directo_api_documentation.pdf` REST API → CSV export)
- Header: `PoNumber`←`number`, `OrderDate`←`date` (+ `DateFormat`), `BuyerName`←`customer_name`, `Currency`←`currency`
- Lines: `BuyerItemCode`←`row_item`, `Description`←`row_description`, `Quantity`←`row_quantity`, `Unit`←`unit`, `UnitPrice`←`row_price`; `LineNumber` omitted (optional — no line-number column in either export)

> ⚠️ **Both ERPs are REST/JSON APIs.** The templates assume the buyer exports the PO to **CSV** with
> these field names as headers. The exact header strings MUST be verified against one real export
> before relying on a template — the apply flow shows the mapping for review/edit, so a mismatch is
> visible and fixable in-editor.

## Backend (`ProcuLink`)
- **Fixtures:** `ProcuLink.Api/Fixtures/po-templates/erply.json`, `directo.json`. Each =
  `{ id, erp, name, description, config: PoMappingConfig }`. `CopyToOutputDirectory=PreserveNewest`.
- **DTO:** `StarterTemplateDto { string Id; string Erp; string Name; string Description; PoMappingConfig Config; }`.
- **Service:** `IStarterTemplateService.GetAll()` reads + caches the fixtures (deserialize once). Lives in `ProcuLink.Api/Services` (it reads Api content-root fixtures). Org-agnostic.
- **Endpoint:** `GET /api/po-mapping-templates` (same auth scheme as other controllers; no org scoping — read-only static data) → `StarterTemplateDto[]`.

## Frontend (`project-proculink`)
- `src/lib/api-client.ts`: `getPoMappingTemplates(): Promise<StarterTemplate[]>` (+ a mock branch under `isApiMockMode` returning the two templates).
- `src/components/bridge/PoMappingEditor.tsx`: a **"Start from template ▾"** control near the mapping table. On select → fetch (TanStack Query) → load the chosen `config` into the editor's existing local mapping state. Inline note: *"Loaded the {Erply} starter — check the column names against your export, then Save."* User saves via the existing PUT. **Does not auto-save.**

## Testing
- **Backend (`ProcuLink.Api.Tests`):** each fixture deserializes to a valid `PoMappingConfig`;
  round-trip through `PoMappingEngine.Apply` with a synthetic header + line row (dict keys = the
  template's `ExternalField` values) yields a `MappedOrder` with all canonical fields non-null;
  `GetAll()` returns exactly `erply` + `directo`.
- **Frontend:** `bunx tsc --noEmit` clean; the editor renders the control and loading a template
  populates the mapping rows.

## Out of scope
DB persistence of templates; auto-detecting the ERP from an uploaded file; non-CSV (XML/JSON) source
parsing; per-supplier template defaults. The Erply/Directo CSV column strings are best-effort pending
real-export verification.
