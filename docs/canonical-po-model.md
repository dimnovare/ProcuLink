# ProcuLink — Canonical Purchase Order Model

_Last updated: 2026-05-27. Describes the intermediate model that all parsers produce
and all output transformers consume._

---

## Overview

The canonical PO model has two layers:

1. **Parse layer** — `ParsedOrder` / `ParsedOrderLine` (records in `ProcuLink.Transform.Parsing`).
   Produced by `IPurchaseOrderParser` implementations. Raw values; no supplier-code resolution.

2. **Entity layer** — `PurchaseOrderEntity` / `PurchaseOrderLineEntity` (classes in `ProcuLink.Core.Entities`).
   Persisted to Postgres. Supplier codes resolved via `ItemMappingService` + AI suggestions.
   Output transformers consume this layer.

The `PurchaseOrderEntity.CanonicalJson` column (JSONB) stores the parse-layer snapshot so it
can be re-inspected or re-processed without re-uploading the source file.

---

## Parse Layer

### `ParsedOrder`

```
namespace ProcuLink.Transform.Parsing
record ParsedOrder(
    string? PoNumber,
    DateTime? OrderDate,
    string? BuyerName,
    string? Currency,
    IReadOnlyList<ParsedOrderLine> Lines
)
```

### Header Fields

| Field | Type | Required | Business Rule |
|---|---|---|---|
| `PoNumber` | `string?` | recommended | Max 50 chars; null when source file does not include a PO number field |
| `OrderDate` | `DateTime?` | recommended | Parsers try: `yyyy-MM-dd`, `dd/MM/yyyy`, `MM/dd/yyyy`, `yyyy-MM-ddTHH:mm:ss`, `M/d/yyyy`, `d.M.yyyy`; null on failure |
| `BuyerName` | `string?` | optional | Display/audit; not used for routing or mapping |
| `Currency` | `string?` | recommended | ISO 4217 three-letter code; uppercased by PDF parser; not validated at parse time |
| `Lines` | `IReadOnlyList<ParsedOrderLine>` | yes | At least one element expected; an empty list is valid but unusual |

---

### `ParsedOrderLine`

```
namespace ProcuLink.Transform.Parsing
record ParsedOrderLine(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal? UnitPrice
)
```

### Line Fields

| Field | Type | Required | Business Rule |
|---|---|---|---|
| `LineNumber` | `int` | yes | Auto-incremented from 1 if absent in source; must be positive |
| `BuyerItemCode` | `string` | yes | Buyer's own item code; looked up in `item_mappings` to resolve `SupplierItemCode`; never sourced from the supplier |
| `Description` | `string?` | optional | Raw text from source; not transformed or validated |
| `Quantity` | `decimal` | yes | Defaults to `0m` if unparseable; culture-invariant (`CultureInfo.InvariantCulture`) |
| `Unit` | `string?` | optional | Unit of measure string (e.g. `EA`, `PCS`, `KG`, `M`); passed through as-is |
| `UnitPrice` | `decimal?` | recommended | Null if absent in source; required by all current output transformers |

---

## Entity Layer

### `PurchaseOrderEntity`

Stored in the `purchase_orders` table (Postgres).

| Field | Type | Required | Business Rule |
|---|---|---|---|
| `Id` | `Guid` | yes | PK; generated on creation |
| `OrgId` | `Guid` | yes | FK → `organisations`; all EF queries must filter by this |
| `SupplierId` | `Guid` | yes | FK → `suppliers` within the org |
| `PoNumber` | `string` | yes | Defaults to empty string if parse returned null; displayed in UI |
| `OrderDate` | `DateOnly` | yes | Mapped from `DateTime?` to `DateOnly`; defaults to today if parse returned null |
| `Currency` | `string` | yes | Defaults to empty string if parse returned null |
| `Status` | `string` | yes | State machine: `pending_parse` → `parsing` → `pending_review` → `ready` → `transforming` → `ready_to_deliver` → `delivering` → `delivered` / `delivery_failed` / `failed` |
| `SourceFileKey` | `string?` | optional | R2 object key of the uploaded source file |
| `CanonicalJson` | `JsonDocument?` | optional | JSONB snapshot of the `ParsedOrder` at parse time |
| `CreatedAt` | `DateTime` | yes | UTC; set on insert |
| `UpdatedAt` | `DateTime` | yes | UTC; set on every update |

### `PurchaseOrderLineEntity`

Stored in the `purchase_order_lines` table (Postgres).

| Field | Type | Required | Business Rule |
|---|---|---|---|
| `Id` | `Guid` | yes | PK |
| `OrderId` | `Guid` | yes | FK → `purchase_orders` |
| `LineNumber` | `int` | yes | From `ParsedOrderLine.LineNumber` |
| `BuyerItemCode` | `string` | yes | From `ParsedOrderLine.BuyerItemCode`; lookup key for mapping |
| `SupplierItemCode` | `string?` | conditional | Null until resolved via `ItemMappingService`; must be non-null before transform |
| `Description` | `string?` | optional | From `ParsedOrderLine.Description` |
| `Quantity` | `decimal` | yes | From `ParsedOrderLine.Quantity` |
| `Unit` | `string?` | optional | From `ParsedOrderLine.Unit` |
| `UnitPrice` | `decimal` | yes | From `ParsedOrderLine.UnitPrice ?? 0m`; output transformers use this value |
| `Confidence` | `float` | yes | `1.0` for deterministic mapping lookups; lower for AI suggestions |
| `NeedsReview` | `bool` | yes | `true` when `SupplierItemCode` could not be resolved deterministically; blocks transform |
| `AiSuggestedSupplierItemCode` | `string?` | optional | Set when AI suggests a code; cleared on manual resolution |
| `AiSuggestionConfidence` | `float?` | optional | AI provider confidence score; `0.0`–`1.0` |
| `AiSuggestionReason` | `string?` | optional | Human-readable explanation from AI provider |
| `AiSuggestionProvenance` | `string?` | optional | Model ID / provider name (e.g. `openai/gpt-4o-mini`) |

---

## Transform Layer Inputs

Output transformers (`ITransformService` implementations) operate on `PurchaseOrderEntity` with
its `Lines` navigation property loaded. Before calling any transformer the service **must** verify:

- `line.NeedsReview == false` for every line
- `line.SupplierItemCode` is non-null and non-empty for every line

Failure throws `TransformValidationException` with the list of unresolved line numbers (HTTP 422).

---

## CanonicalJson Schema (informational)

The `CanonicalJson` column stores the `ParsedOrder` serialized to JSON using
`System.Text.Json` with camelCase naming. Example:

```json
{
  "poNumber": "PO-12345",
  "orderDate": "2024-01-15T00:00:00",
  "buyerName": "Acme Procurement",
  "currency": "EUR",
  "lines": [
    {
      "lineNumber": 1,
      "buyerItemCode": "BUYER-ABC-001",
      "description": "Widget Type A",
      "quantity": 10.0,
      "unit": "EA",
      "unitPrice": 125.00
    }
  ]
}
```

Fields are nullable at parse time. The `orderDate` field is serialized as an ISO 8601
`DateTime` string; when mapped to `PurchaseOrderEntity.OrderDate` it is truncated to `DateOnly`.
