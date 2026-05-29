# PO Field Mapping Engine — Design Spec

> **Phase 4 Group D**
>
> **Scope:** Per-supplier mapping templates that transform an incoming client PO CSV into
> ProcuLink's canonical order format. Covers the full purchase order (header + line fields)
> with 8 field manipulators. Delivery/posting configuration is Group D2 (separate spec).

**Goal:** Replace hardcoded CSV column aliases with a per-supplier configurable mapping
template — stored as JSONB — so any client's PO CSV layout can be transformed into the
canonical order format without code changes. Non-developers configure mappings through a
visual UI; advanced users can toggle to raw JSON.

**Architecture:** New `SupplierPoMapping` entity (JSONB config) + `IPoMappingService` for
CRUD + `PoMappingEngine` in `ProcuLink.Transform` that applies the template to raw CSV rows.
`ParseOrderJob` gains a template-aware code path; existing `CsvOrderParser` path remains as
fallback for suppliers with no template.

**Tech Stack:** .NET 8 / ASP.NET Core / EF Core + Npgsql JSONB (backend);
Next.js 15 App Router, TanStack Query v5, shadcn/ui (frontend)

---

## Relationship to Existing Code

- **`CsvOrderParser`** — kept unchanged. Used as fallback when no `SupplierPoMapping` exists.
- **`ItemMapping` / `IItemMappingService`** — kept unchanged. Item code resolution
  (buyer code → supplier code lookup) remains a separate post-mapping step in `ParseOrderJob`.
- **`SupplierProfile.DestinationConfig`** — unchanged. Delivery configuration is Group D2.

---

## Canonical PO Schema

These are the target fields every mapping template maps **to**. They are fixed — new fields
require a spec change.

### Header fields (one value per PO)

| Field | Type | Notes |
|-------|------|-------|
| `PoNumber` | `string` | Required |
| `OrderDate` | `string` → date | Parsed after `DateFormat` manipulator |
| `BuyerName` | `string` | Buyer company name |
| `Currency` | `string` | ISO 4217, e.g. `EUR` |
| `BillingAddress` | `string` | Full address — often built with `Concat` |
| `ShippingAddress` | `string` | Full delivery address |
| `PaymentTerms` | `string?` | e.g. `Net 30` |
| `Notes` | `string?` | Free-text comment |

### Line fields (one value per line row)

| Field | Type | Notes |
|-------|------|-------|
| `BuyerItemCode` | `string` | Required — used for supplier code resolution |
| `Description` | `string?` | Product description |
| `Quantity` | `decimal` | Required |
| `Unit` | `string?` | e.g. `pcs`, `kg`, `m` |
| `UnitPrice` | `decimal?` | Net price per unit |

---

## Config JSON Shape

Stored in `SupplierPoMapping.ConfigJson` (JSONB). Each field entry specifies either
`externalField` (read from a named CSV column) or `fixedValue` (hard-coded), with an
optional `fieldManipulators` chain applied in order.

```json
{
  "hasHeaderRecord": true,
  "separator": ",",
  "header": {
    "PoNumber":   { "externalField": "Order No" },
    "OrderDate":  {
      "externalField": "Date",
      "fieldManipulators": [{ "name": "DateFormat", "parameters": ["dd/MM/yyyy"] }]
    },
    "BuyerName":  { "externalField": "Company" },
    "Currency":   { "fixedValue": "EUR" },
    "BillingAddress": {
      "externalField": "BillStreet",
      "fieldManipulators": [
        { "name": "Concat", "parameters": ["@", ", ", "@BillCity", " ", "@BillZip"] }
      ]
    },
    "ShippingAddress": { "externalField": "ShipAddr" },
    "PaymentTerms": { "externalField": "Terms" },
    "Notes":       { "externalField": "Comments" }
  },
  "lines": {
    "BuyerItemCode": { "externalField": "Item Code" },
    "Description":   { "externalField": "Product Name" },
    "Quantity": {
      "externalField": "Qty",
      "fieldManipulators": [{ "name": "Replace", "parameters": [",", "."] }]
    },
    "Unit":      { "externalField": "UOM" },
    "UnitPrice": {
      "externalField": "Price",
      "fieldManipulators": [{ "name": "Replace", "parameters": [",", "."] }]
    }
  }
}
```

---

## Backend

### Data model

**New entity: `ProcuLink.Core/Entities/SupplierPoMapping.cs`**

```csharp
public class SupplierPoMapping
{
    public Guid         Id         { get; set; }
    public Guid         OrgId      { get; set; }
    public Guid         SupplierId { get; set; }
    public JsonDocument ConfigJson { get; set; } = null!;  // JSONB
    public DateTime     CreatedAt  { get; set; }
    public DateTime     UpdatedAt  { get; set; }

    public Organisation Organisation { get; set; } = null!;
    public Supplier     Supplier     { get; set; } = null!;
}
```

EF mapping: `HasColumnType("jsonb")`. Unique index on `(org_id, supplier_id)`.
New migration: `AddSupplierPoMappings`.

### Config POCOs — `ProcuLink.Core/Services/Mapping/`

```csharp
public record PoMappingConfig(
    bool HasHeaderRecord,
    string Separator,
    Dictionary<string, FieldMappingEntry> Header,
    Dictionary<string, FieldMappingEntry> Lines
);

public record FieldMappingEntry(
    string? ExternalField,
    string? FixedValue,
    List<ManipulatorEntry>? FieldManipulators
);

public record ManipulatorEntry(string Name, string[] Parameters);
```

### `IPoMappingService` — `ProcuLink.Core/Services/Mapping/`

```csharp
public interface IPoMappingService
{
    Task<PoMappingConfig?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct);
    Task SaveAsync(Guid orgId, Guid supplierId, PoMappingConfig config, CancellationToken ct);
}
```

`PoMappingService` in `ProcuLink.Infrastructure/Services/` — upserts the row, serialises
`PoMappingConfig` to `JsonDocument` on save, deserialises on read.

### API endpoints (added to `SuppliersController`)

All verify supplier belongs to org → 404 otherwise.

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/suppliers/{id}/po-mapping` | Return config (404 if none exists) |
| `PUT` | `/api/suppliers/{id}/po-mapping` | Create or replace config |
| `GET` | `/api/suppliers/{id}/po-mapping/export` | Download as `mapping-{name}.json` attachment |
| `POST` | `/api/suppliers/{id}/po-mapping/import` | Upload `.json` file, validate structure, save |

`PUT` body: `PoMappingConfig` JSON. Returns 200 with saved config.
`POST /import`: `multipart/form-data` with `file` field. Validates required keys exist
(`header`, `lines`, `separator`) → 400 with message if invalid. Returns 200 with config.

---

## Transform Engine — `ProcuLink.Transform/Mapping/`

### `IFieldManipulator`

```csharp
public interface IFieldManipulator
{
    string Name { get; }
    // rowContext = all raw CSV column values for the current row (for cross-field refs)
    string Apply(string value, string[] parameters,
                 IReadOnlyDictionary<string, string> rowContext);
}
```

### 8 manipulators

| Class | `Name` | Behaviour | Parameters |
|-------|--------|-----------|------------|
| `ReplaceManipulator` | `Replace` | Replace all occurrences of A with B | `[",", "."]` |
| `TrimManipulator` | `Trim` | Strip chars (default: whitespace) | `[]` or `["-", "_"]` |
| `DateFormatManipulator` | `DateFormat` | Parse with given format → ISO 8601 | `["dd/MM/yyyy"]` |
| `ConcatManipulator` | `Concat` | Join parts: `@` = ExternalField value, `@Col` = other column | `["@", ", ", "@City"]` |
| `FallbackManipulator` | `Fallback` | Use named column when primary is empty | `["OtherColumn"]` |
| `SplitManipulator` | `Split` | Split by delimiter, return segment at index | `[" - ", "0"]` |
| `MultiplyManipulator` | `Multiply` | Multiply decimal value by factor | `["0.453592"]` |
| `DivideManipulator` | `Divide` | Divide decimal value by factor | `["100"]` |

`ManipulatorRegistry` — static `Dictionary<string, IFieldManipulator>` keyed by `Name`
(case-insensitive). Registered at startup.

### `PoMappingEngine`

```csharp
public sealed class PoMappingEngine
{
    // rows: each row is a dict of CSV column name → raw string value
    public MappedOrder Apply(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        PoMappingConfig config);
}
```

**Header extraction:** For each header field entry, if `FixedValue` is set use it directly;
otherwise scan all rows for the first non-empty value in the `ExternalField` column, then
apply the manipulator chain with `rowContext = rows[0]`.

**Line extraction:** For each row, resolve each line field the same way (using that row
as `rowContext`). Skip rows where `BuyerItemCode` resolves to empty.

**Output:** `MappedOrder` record in `ProcuLink.Transform/Mapping/`:

```csharp
public record MappedOrder(
    string?  PoNumber,
    DateTime? OrderDate,
    string?  BuyerName,
    string?  Currency,
    string?  BillingAddress,
    string?  ShippingAddress,
    string?  PaymentTerms,
    string?  Notes,
    IReadOnlyList<MappedOrderLine> Lines
);

public record MappedOrderLine(
    int      LineNumber,
    string   BuyerItemCode,
    string?  Description,
    decimal  Quantity,
    string?  Unit,
    decimal? UnitPrice
);
```

`MappedOrder` serialises to the same `CanonicalJson` shape as `ParsedOrder`, extended with
`billingAddress`, `shippingAddress`, `paymentTerms`, `notes`. Everything downstream reads
from `CanonicalJson` and is unaffected.

### `ParseOrderJob` update

```
Download file from R2
↓
Load PoMappingConfig? for (orgId, supplierId)
├── Config exists → read raw CSV rows (CsvHelper, no ClassMap)
│                  → PoMappingEngine.Apply → MappedOrder → CanonicalJson
└── No config    → CsvOrderParser.ParseAsync → ParsedOrder → CanonicalJson  (unchanged)
↓
Item code resolution via IItemMappingService (unchanged, runs after either path)
```

---

## Frontend

### Supplier detail page

`src/app/(app)/library/suppliers/[id]/page.tsx` gets a **"PO Mapping"** tab added
alongside existing tabs.

### `src/components/bridge/PoMappingEditor.tsx`

`'use client'` component receiving `supplierId: string`. Loads config via `useQuery`
(key: `["po-mapping", supplierId]`). Local state mirrors the config for editing.

**Layout:**

1. **Toolbar (top right):** Save button (calls `savePoMapping` via `useMutation`, shows
   spinner while pending, toast on success/error) · Export JSON (downloads file) ·
   Import JSON (hidden `<input type="file" accept=".json">`) · "View JSON" toggle
   (switches entire editor to a read-only `<pre>` of the current config — not editable,
   just for inspection)

2. **File settings strip:** Separator dropdown (`,` / `;` / Tab) · Has Header Record toggle

3. **"Order Header" card:** One row per canonical header field (8 rows). Each row:
   - Field name label (bold)
   - Source toggle: "Column" / "Fixed value"
   - If Column: text input labelled "CSV column name"
   - If Fixed value: text input labelled "Value"
   - Manipulators chevron → expands an inline list of added manipulators with
     an "Add manipulator" button

4. **"Line Items" card:** Same structure for 5 line fields.

5. **Manipulator row** (inside expanded section):
   - `<Select>` with 8 options
   - Parameter inputs that change based on selected manipulator:
     - `Replace` → "Find" + "Replace with" text inputs
     - `Trim` → optional "Characters" input (placeholder: "whitespace")
     - `DateFormat` → "Format" input (placeholder: `dd/MM/yyyy`)
     - `Concat` → dynamic list of parts — each part is a text input prefixed with
       a type toggle ("Literal" / "Column ref"); "Add part" button
     - `Fallback` → "Fallback column" text input
     - `Split` → "Delimiter" + "Index" inputs
     - `Multiply` / `Divide` → "Factor" numeric input
   - × remove button (right side)

Unmapped fields show a muted "Not mapped" placeholder — not an error, just empty.

### `src/lib/api-client.ts` additions

```typescript
export async function getPoMapping(supplierId: string): Promise<PoMappingConfig | null>
export async function savePoMapping(supplierId: string, config: PoMappingConfig): Promise<PoMappingConfig>
export async function exportPoMapping(supplierId: string): Promise<void>   // blob download
export async function importPoMapping(supplierId: string, file: File): Promise<PoMappingConfig>
```

Mock: `getPoMapping` returns a sample config with `PoNumber`, `OrderDate`, `Currency`
(fixed: `EUR`) in header and `BuyerItemCode`, `Quantity` (with Replace `,`→`.`) in lines.
`exportPoMapping` builds a blob from the mock config and triggers `<a download>`.

### TypeScript types — `src/types/procurement.ts` additions

```typescript
export interface PoMappingConfig {
  hasHeaderRecord: boolean;
  separator: string;
  header: Record<string, FieldMappingEntry>;
  lines:  Record<string, FieldMappingEntry>;
}

export interface FieldMappingEntry {
  externalField?:    string;
  fixedValue?:       string;
  fieldManipulators?: ManipulatorEntry[];
}

export interface ManipulatorEntry {
  name:       string;
  parameters: string[];
}
```

---

## File Structure

| Action | Path |
|--------|------|
| Create | `ProcuLink.Core/Entities/SupplierPoMapping.cs` |
| Create | `ProcuLink.Core/Services/Mapping/PoMappingConfig.cs` |
| Create | `ProcuLink.Core/Services/Mapping/IPoMappingService.cs` |
| Create | `ProcuLink.Infrastructure/Services/PoMappingService.cs` |
| Create | `ProcuLink.Transform/Mapping/IFieldManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/ReplaceManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/TrimManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/DateFormatManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/ConcatManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/FallbackManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/SplitManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/MultiplyManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/Manipulators/DivideManipulator.cs` |
| Create | `ProcuLink.Transform/Mapping/MappedOrder.cs` |
| Create | `ProcuLink.Transform/Mapping/PoMappingEngine.cs` |
| Create | `src/components/bridge/PoMappingEditor.tsx` |
| Create | `src/types/procurement.ts` (additions) |
| Modify | `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — add `SupplierPoMappings` DbSet + EF config |
| Modify | `ProcuLink.Api/Controllers/SuppliersController.cs` — add 4 endpoints |
| Modify | `ProcuLink.Api/Program.cs` — register `IPoMappingService` |
| Modify | `ProcuLink.Worker/Jobs/ParseOrderJob.cs` — template-aware code path |
| Modify | `src/lib/api-client.ts` — add 4 functions |
| Modify | `src/app/(app)/library/suppliers/[id]/page.tsx` — add PO Mapping tab |
| Create | EF migration `AddSupplierPoMappings` |

---

## Error Handling

| Scenario | Response |
|----------|----------|
| Supplier not found or wrong org | `404 Not Found` |
| Import file missing or empty | `400 { error: "No file provided" }` |
| Import JSON missing required keys | `400 { error: "Invalid mapping config: missing 'lines'" }` |
| ExternalField column not found in CSV | Row treated as if field is empty (no crash) |
| Manipulator receives non-numeric input for Multiply/Divide | Returns original value unchanged; warning logged |
| Save network error (frontend) | Destructive toast; editor stays open |
| Import file parse error (frontend) | Destructive toast; editor stays open |

---

## Testing

**`ProcuLink.Transform` unit tests:**

- Each manipulator in isolation: valid input, empty input, edge cases
  (e.g. `Replace` with no match, `DateFormat` with unparseable string,
  `Concat` with missing `@Col` reference — returns empty string for that part)
- `PoMappingEngine.Apply`: sample config + sample rows → verify all header and line fields
  extracted correctly; verify `FixedValue` overrides `ExternalField`; verify missing column
  returns empty string (not exception); verify manipulator chain applied in order

**`ProcuLink.Api` integration tests:**

- `GET /api/suppliers/{id}/po-mapping` — 404 when no config; 200 with config when exists
- `PUT /api/suppliers/{id}/po-mapping` — creates on first call; replaces on second call
- `POST /api/suppliers/{id}/po-mapping/import` — valid JSON → 200; missing keys → 400;
  wrong org → 404
- `GET /api/suppliers/{id}/po-mapping/export` — Content-Disposition attachment header present

**Frontend:**

- `PoMappingEditor`: renders all 8 header fields and 5 line fields; column/fixed toggle
  switches input; manipulator add/remove works; save calls `savePoMapping` with correct shape;
  export triggers download; import reads file and populates editor
