# Bulk Mapping Import/Export — Design Spec

> **Phase 4 Group D**

**Goal:** Let users bulk-load item code mappings from an arbitrary CSV file and download existing mappings as CSV, with a two-step import dialog that lets them pick which CSV columns map to buyer and supplier codes.

**Architecture:** Two new endpoints on the existing `SuppliersController` (no new controller). A small static `CsvHelper` class handles CSV parsing. Frontend adds an `ImportMappingsDialog` component and two buttons to the existing `MappingsPage`.

**Tech Stack:** .NET 8 / ASP.NET Core / EF Core (backend); Next.js 15 App Router, TanStack Query v5, shadcn/ui (frontend)

---

## Backend

### New endpoints (added to `SuppliersController`)

#### `POST /api/suppliers/{id}/mappings/import`

- Auth: `[Authorize]` (org-scoped via `ICurrentTenantService`)
- Content-type: `multipart/form-data`
- Query parameters: `buyerColumn` (string), `supplierColumn` (string) — the CSV header names to use
- Form field: `file` (IFormFile)

**Logic:**
1. Verify the supplier exists and belongs to the org → `404` if not.
2. Parse the CSV header row to find zero-based column indices for `buyerColumn` and `supplierColumn` → `400 { error: "Column '{name}' not found in CSV header" }` if either is missing.
3. For each data row: read both cells; skip if either is empty/whitespace (count as `skipped`); otherwise call `_mappingService.UpsertAsync(orgId, supplierId, buyer, supplier, MappingSource.Imported, ct)` (count as `imported`).
4. Return `200 { imported: N, skipped: M }`.

#### `GET /api/suppliers/{id}/mappings/export`

- Auth: `[Authorize]` (org-scoped)
- No request body or query params.

**Logic:**
1. Verify supplier belongs to org → `404` if not.
2. Fetch all mappings via `_mappingService.GetForSupplierAsync(orgId, supplierId, ct)`.
3. Build CSV in memory: header line `buyer_item_code,supplier_item_code` followed by one line per mapping (`BuyerItemCode,SupplierItemCode`). Quote any cell that contains a comma or double-quote (double-quote escaped as `""`).
4. Return `File(bytes, "text/csv", $"mappings-{supplier.Name}.csv")` with `Content-Disposition: attachment`.

### New file: `ProcuLink.Api/Helpers/CsvHelper.cs`

Static helper with two methods:

```csharp
// Parses a CSV line into cells, handling double-quote-wrapped fields.
// Trims leading/trailing whitespace from each cell after unquoting.
public static string[] ParseLine(string line)

// Escapes a cell value for CSV: wraps in double-quotes if it contains
// a comma, double-quote, or newline; escapes internal double-quotes as "".
public static string EscapeCell(string value)
```

No external CSV library dependency — item codes will not contain commas in practice, but the helper handles quoted fields correctly for robust export.

---

## Frontend

### New file: `src/components/bridge/ImportMappingsDialog.tsx`

A shadcn `<Dialog>` controlled by `open` / `onOpenChange` props. Accepts `supplierId: string` as a prop.

**Phase 1 — Column selection:**
- A `<input type="file" accept=".csv">` inside the dialog body.
- On file selection: use `FileReader.readAsText()` to read the file, split the first line by comma, trim whitespace and double-quotes to extract header names.
- Render two `<Select>` components: "Buyer item code column" and "Supplier item code column", populated with the parsed header names.
- "Import" button is disabled until both selects have a value and the file is present.

**Phase 2 — Importing:**
- On "Import" click: call `importMappings()` via `useMutation`.
- Button shows a `<Loader2>` spinner while pending.
- On success: close dialog, invalidate `["mappings", supplierId]`, show toast: `"Imported N rows (M skipped)"`.
- On error: show destructive toast with the error message; dialog stays open so the user can retry.
- Reset all local state (file, headers, column choices) when dialog closes.

### Changes to `src/views/MappingsPage.tsx`

The existing mappings card `<CardHeader>` gets a right-aligned button group added, visible only when a supplier is selected:

- **"Export CSV"** (`<Download>` icon): calls `exportMappings(selectedId)`. Disabled if `loadingMappings` or `mappings.length === 0`.
- **"Import CSV"** (`<Upload>` icon): sets `importOpen(true)` to open `ImportMappingsDialog`. Always enabled when a supplier is selected.

`<ImportMappingsDialog supplierId={selectedId} open={importOpen} onOpenChange={setImportOpen} />` rendered unconditionally at the bottom of the component (below the cards), with `supplierId` defaulting to `""` when nothing is selected (dialog will not be opened in that case).

### Changes to `src/lib/api-client.ts`

Two new named exports following the existing `USE_MOCK` / `authHeader()` pattern:

```typescript
export async function importMappings(
  supplierId: string,
  file: File,
  buyerColumn: string,
  supplierColumn: string,
): Promise<{ imported: number; skipped: number }>
```

- Mock: returns `{ imported: 2, skipped: 0 }` after 800 ms delay.
- Real: `POST /api/suppliers/{supplierId}/mappings/import?buyerColumn=…&supplierColumn=…` with a `FormData` body containing the file; parses JSON response.

```typescript
export async function exportMappings(supplierId: string): Promise<void>
```

- Mock: builds a blob from `mockMappings[supplierId]` (header + rows), creates an object URL, clicks a hidden `<a download="mappings-mock.csv">`, revokes the URL.
- Real: `GET /api/suppliers/{supplierId}/mappings/export` with auth header; receives blob; same download trigger pattern.

---

## File Structure

| Action | Path |
|--------|------|
| Create | `ProcuLink.Api/Helpers/CsvHelper.cs` |
| Modify | `ProcuLink.Api/Controllers/SuppliersController.cs` — add 2 endpoints |
| Create | `src/components/bridge/ImportMappingsDialog.tsx` |
| Modify | `src/views/MappingsPage.tsx` — add import/export buttons + dialog |
| Modify | `src/lib/api-client.ts` — add `importMappings`, `exportMappings` |

---

## Error Handling

| Scenario | Response |
|----------|----------|
| Supplier not found or wrong org | `404 Not Found` |
| `buyerColumn` or `supplierColumn` not in CSV header | `400 { error: "Column 'X' not found in CSV header" }` |
| File missing or empty | `400 { error: "No file uploaded" }` |
| Row with empty buyer or supplier cell | Skipped, counted in `skipped` |
| Import network error (frontend) | Destructive toast; dialog stays open |
| Export network error (frontend) | Destructive toast |

---

## Testing

**Backend (`ProcuLink.Api.Tests` or integration tests):**
- `CsvHelper.ParseLine` unit tests: plain cells, quoted cells, cells with embedded commas, embedded double-quotes.
- `CsvHelper.EscapeCell` unit tests: plain value, value with comma, value with double-quote.
- Import endpoint: valid CSV → correct `imported`/`skipped` counts; missing column → 400; wrong org → 404.
- Export endpoint: returns CSV with correct headers and rows; empty supplier → header-only CSV; wrong org → 404.

**Frontend:**
- `ImportMappingsDialog`: header parsing from a sample CSV string; column select populates correctly; submit calls `importMappings` with correct args; success closes dialog and shows toast; error keeps dialog open.
- `exportMappings` mock: blob is created and download triggered.
