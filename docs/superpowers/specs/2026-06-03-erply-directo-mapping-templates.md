# Erply & Directo Starter PO Mapping Templates

**Date:** 2026-06-03 · **Status:** Implemented
**Why:** the #1 sales-leverage item for the Baltic Erply/Directo wedge — pre-built column
mappings so a new customer's first PO delivery takes minutes instead of a from-scratch mapping
build. Cuts first-supplier setup from ~45 min to <15 min.

This note supersedes/extends the earlier design at
`docs/superpowers/specs/2026-05-30-erply-directo-starter-templates-design.md`. The list-endpoint
and fixtures from that design were already shipped; this adds a **server-side one-click apply**.

---

## What was built

### Static starter-template fixtures (already shipped, unchanged)
Five `PoMappingConfig` templates embedded in `ProcuLink.Api.dll` (no DB):

| File | `id` | ERP |
|---|---|---|
| `ProcuLink.Api/Fixtures/po-templates/generic-csv.json` | `generic-csv` | Generic CSV |
| `ProcuLink.Api/Fixtures/po-templates/buyer-excel.json` | `buyer-excel` | Buyer Excel |
| `ProcuLink.Api/Fixtures/po-templates/cxml-orderrequest.json` | `cxml-orderrequest` | cXML |
| `ProcuLink.Api/Fixtures/po-templates/erply.json` | `erply` | **Erply** |
| `ProcuLink.Api/Fixtures/po-templates/directo.json` | `directo` | **Directo** |

Served by:
- `IStarterTemplateService` / `StarterTemplateService` (`ProcuLink.Api/Services/StarterTemplates/`) —
  loads + caches the embedded JSON once (`Lazy`, thread-safe), org-agnostic.
- `GET /api/po-mapping-templates` (`PoMappingTemplatesController`) — returns the full template set
  (`StarterTemplateDto[]`); read-only, no org scoping (global static data).

### NEW: server-side apply endpoint
`POST /api/suppliers/{id}/po-mapping/apply-template` with body `{ "templateId": "erply" }`.

- Org-scoped (`ICurrentTenantService.OrganisationId`); 404 if the supplier is unknown or belongs to
  another org (cross-tenant safe).
- Looks up the starter template by `id` (case-insensitive); 404 for an unknown template id;
  400 for a blank `templateId`.
- Copies the template's `PoMappingConfig` into the supplier's **existing** PO mapping store via
  `IPoMappingService.UpsertAsync` — i.e. identical persistence to a manual `PUT
  /api/suppliers/{id}/po-mapping` of that config. **No new table, no migration.**
- Returns the saved `PoMappingConfig` (200) so the editor can show it for review/edit.
- Idempotent: re-applying replaces the supplier's config (upsert).

Why server-side *and* the existing client-side load: the list endpoint already lets the editor
preview a template before saving; the new endpoint gives a true one-click "apply Erply starter to
this supplier" with one round trip, which is the lowest-friction path for the sales/onboarding flow.
The user can still edit + re-save via the existing `PUT` afterwards.

---

## Assumed column → canonical mappings

> ⚠️ **STARTER ASSUMPTIONS.** Both Erply and Directo are REST/JSON APIs. These templates assume the
> buyer exports the PO to **CSV** with the field names below as headers. The exact header strings
> MUST be verified against one real export before relying on a template — the apply flow shows the
> mapping for review/edit, so a mismatch is visible and fixable in-editor.

### Erply (`getPurchaseDocuments` export → CSV)

| Canonical field | Source column | Notes |
|---|---|---|
| `PoNumber` | `number` | Erply document `number` |
| `OrderDate` | `date` | `DateFormat yyyy-MM-dd → yyyy-MM-dd` (identity; adjust if export uses another format) |
| `BuyerName` | `clientName` | |
| `Currency` | `currencyCode` | |
| `BuyerItemCode` | `code` | product `code` on the row |
| `Description` | `itemName` | |
| `Quantity` | `amount` | Erply line qty field is `amount` |
| `Unit` | `unitName` | |
| `UnitPrice` | `price` | |
| `LineNumber` | *(omitted)* | optional — no line-number column in the export |

### Directo (REST API export → CSV)

| Canonical field | Source column | Notes |
|---|---|---|
| `PoNumber` | `number` | |
| `OrderDate` | `date` | `DateFormat yyyy-MM-dd → yyyy-MM-dd` (identity; adjust to real export format) |
| `BuyerName` | `customer_name` | |
| `Currency` | `currency` | |
| `BuyerItemCode` | `row_item` | Directo line fields are `row_`-prefixed |
| `Description` | `row_description` | |
| `Quantity` | `row_quantity` | |
| `Unit` | `unit` | |
| `UnitPrice` | `row_price` | |
| `LineNumber` | *(omitted)* | optional — no line-number column in the export |

Canonical field set is authoritative from `PoMappingEngine.Apply`:
header `PoNumber, OrderDate, BuyerName, Currency`; line `LineNumber, BuyerItemCode, Description,
Quantity, Unit, UnitPrice`.

---

## How to apply a template

1. `GET /api/po-mapping-templates` → list available starters (id, erp, name, description, config).
2. `POST /api/suppliers/{supplierId}/po-mapping/apply-template` with `{ "templateId": "erply" }`
   (or `"directo"`). The starter config is persisted onto the supplier and returned.
3. Review in `PoMappingEditor`, correct any column names against the real export, and re-save via
   the existing `PUT /api/suppliers/{supplierId}/po-mapping` if edits are needed.
4. Optionally dry-run with `POST /api/suppliers/{supplierId}/po-mapping/test`.

---

## Tests
`ProcuLink.Api.Tests/Controllers/SuppliersControllerApplyTemplateTests.cs` (9 tests):
- 404 for unknown supplier, cross-org supplier, unknown template id; 400 for blank template id.
- Erply apply (case-insensitive) persists the config and returns it; verified via a second
  `PoMappingService.GetAsync`.
- Directo apply: the **persisted** config round-trips through `PoMappingEngine.Apply` with a
  synthetic Directo header + line row, yielding all canonical fields.
- Re-apply overwrites a pre-existing supplier config.

`ProcuLink.Api.Tests/Services/StarterTemplateServiceTests.cs` (pre-existing) covers fixture
deserialization + engine round-trip for all five templates including erply/directo.

---

## What a human MUST verify before selling on it
1. **Real Erply CSV export headers** — confirm `number`, `date`, `clientName`, `currencyCode`,
   `code`, `itemName`, `amount`, `unitName`, `price` are the actual column names (and the **date
   format** — adjust the `DateFormat` source pattern if it isn't ISO `yyyy-MM-dd`).
2. **Real Directo CSV export headers** — confirm `number`, `date`, `customer_name`, `currency`,
   `row_item`, `row_description`, `row_quantity`, `unit`, `row_price` and the date format.
3. **Separator** — both templates assume comma; confirm the export isn't `;`-delimited (common in
   EU locales). Override `separator` if needed.
4. **Whether either ERP can export PO CSV at all**, or whether the customer pulls via API/Excel —
   if Excel, the `buyer-excel` template may be a better starting point.

## Out of scope
DB persistence of templates; auto-detecting the ERP from an uploaded file; non-CSV (XML/JSON) source
parsing; per-supplier template defaults; ERP-native order modeling. The Erply/Directo CSV column
strings remain best-effort pending real-export verification.
