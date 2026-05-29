# Parse-Failure UX — Design Spec
_2026-05-29_

## Problem

When a purchase order fails to parse (scanned PDF, bad CSV columns, unsupported format), the order lands in `status=failed`. The frontend renders a generic "Order Not Found / Failed to load" gate with no actionable detail. The user has no idea what went wrong or what to do next.

## Solution

Surface a human-readable parse-error message from the backend into the frontend, and render a dedicated `ParseFailedPanel` that clearly distinguishes parse failure from transform/delivery failure and offers a direct "Re-upload" CTA with the supplier pre-selected.

---

## Backend

### 1. `ParseFailureExplain` (new file: `ProcuLink.Api/Services/ParseFailureExplain.cs`)

Static helper class with three pure methods that produce a `(friendly: string, stage: string)` tuple:

| Method | Input | Human-readable message |
|---|---|---|
| `ForEmptyLines(ext)` | `.pdf` | "This PDF looks scanned or image-only — we couldn't extract any text. OCR isn't enabled; export a text-based PDF or upload a CSV/XLSX instead." |
| `ForEmptyLines(ext)` | `.csv`/`.xlsx` | "No line-table columns detected. We couldn't find recognisable item columns (item code, quantity, unit price). Check the header row or map columns using a PO template." |
| `ForEmptyLines(ext)` | anything else | "The document was read but contained zero line items." |
| `ForUnsupportedFormat(ext)` | any | "Unsupported file format '{ext}'. Supported: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT)." |
| `ForException(ext, ex)` | EdifactParseException / X12ParseException | "We couldn't read this EDI file: {ex.Message}" |
| `ForException(ext, ex)` | CxmlParseException / UblParseException | "We couldn't read this XML file: {ex.Message}" |
| `ForException(ext, ex)` | anything else | "Could not parse file: {ex.Message}" |

Stage is always `"parse"`.

### 2. Close audit gaps in `OrderService.ParseStoredFileAsync`

Three failure paths in the method; only one currently writes a `ParseFailed` audit event:

| Path (line) | Current | After |
|---|---|---|
| Unsupported format (~438) | Sets `status=failed`, no audit event | Writes `ParseFailed` audit event with `{ error = ParseFailureExplain.ForUnsupportedFormat(ext), stage = "parse", detail = ex.Message }` |
| Exception catch (~461) | Writes `ParseFailed` with raw `ex.Message` | Upgrades to `ParseFailureExplain.ForException(ext, ex)` for friendly; keeps raw `ex.Message` in `detail` |
| No line items (~469) | Sets `status=failed`, no audit event | Writes `ParseFailed` audit event with `{ error = ParseFailureExplain.ForEmptyLines(ext), stage = "parse", detail = "0 lines parsed" }` |

**`SetOrderFailedAsync`** is already called on all three paths; no change needed there.

### 3. `errorMessage` on `OrderDto`

Add `string? ErrorMessage = null` as a trailing optional parameter to the `OrderDto` record in `ProcuLink.Api/Contracts/OrderDto.cs`.

In `OrdersController.Get`, after fetching the order, when `status` ∈ `{failed, transform_failed, delivery_failed}`:

```csharp
// Query the newest *Failed audit event for this order
var failedAudit = await _db.AuditEvents
    .AsNoTracking()
    .Where(e => e.EntityId == id && e.OrgId == orgId
             && e.EntityType == "Order"
             && (e.Action == "ParseFailed" || e.Action == "TransformFailed" || e.Action == "DeliveryFailed"))
    .OrderByDescending(e => e.CreatedAt)
    .Select(e => e.Payload)
    .FirstOrDefaultAsync(ct);

string? errorMessage = null;
if (failedAudit != null)
{
    try { failedAudit.RootElement.TryGetProperty("error", out var el); errorMessage = el.GetString(); }
    catch { /* ignore */ }
}
```

Pass `errorMessage` into `MapToDto`.

**No DB migration required.** `AuditEvents` table already exists; only the DTO and the query path change.

### 4. Tests (`ProcuLink.Api.Tests`)

- `ParseFailureExplainTests`: unit tests for each branch of all three methods.
- `ParseStoredFileAsyncAuditTests`: mock `IFileStorageService` to return a malformed CSV and verify a `ParseFailed` audit event is written with a `friendly` message (not raw exception message).
- `OrdersControllerGetErrorMessageTests`: create a failed order with a `ParseFailed` audit event, call `GET /api/orders/{id}`, assert `errorMessage` is present and matches the audit payload.

---

## Frontend

### 1. Type extension (`src/types/procurement.ts`)

```ts
export interface Order {
  // … existing fields …
  errorMessage?: string | null;
}
```

### 2. `ParseFailedPanel` (`src/components/bridge/ParseFailedPanel.tsx`)

New component. Props: `{ order: Order; auditEvents?: AuditEvent[]; detectResult?: DetectFormatResult | null }`.

Layout (Bridge Layer tokens — danger red `#C53A3A`, surface `#FFFFFF`, border `#E2E6EE`):

```
┌─────────────────────────────────────────────────────────────────┐
│ [!] Parsing failed                                [PDF chip]   │  ← 3px danger left border
│ ────────────────────────────────────────────────────────────── │
│ This PDF looks scanned or image-only…                           │  ← errorMessage from DTO
│   (fallback: audit event payload.error)                         │
│ ────────────────────────────────────────────────────────────── │
│ Your source file is still stored.                               │
│                                                                 │
│  [Re-upload — try a different format →]                         │  ← primary CTA
│  [← Back to orders]                                             │  ← secondary
└─────────────────────────────────────────────────────────────────┘
```

- **Format chip**: `SrcChip` from `OrderDetailPage` (same token map). Format derived from `order.sourceFileKey` extension. If `detectResult` is provided, append a small confidence pill (e.g. "CSV · 97%").
- **Error message**: prefer `order.errorMessage`; fall back to `auditEvents?.find(e => e.action === "ParseFailed")?.payload?.error as string`.
- **Re-upload CTA**: `href="/upload?supplierId={order.supplierId}"`. Opens upload with same supplier pre-selected.
- **Detect result caching**: on mount, reads `sessionStorage.getItem('detectResult:{order.id}')` and parses it as `DetectFormatResult`.

### 3. `FailedPanel` (same file or adjacent)

Props: `{ order: Order; stage: "transform" | "delivery"; onRedeliver?: () => void; isRedelivering?: boolean }`.

| Stage | Tone | Title | CTA |
|---|---|---|---|
| `transform` | Amber `#C97A14` | "Output generation failed" | "Back to review" — `href="/orders/{id}"` |
| `delivery` | Danger `#C53A3A` | "Delivery to supplier failed" | "Retry delivery" — calls `POST /api/orders/{id}/redeliver` |

Retry delivery: simple `fetch` call in the component; shows spinner while in flight; on success invalidates the `["order", id]` TanStack Query cache so the page refreshes.

### 4. Wire into `OrderDetailPage.tsx`

Insert a block immediately after the existing not-found/network-error gate (before the main render), replacing the hardcoded "Status" text with a proper three-branch gate:

```tsx
if (order.status === "failed") {
  return <ParseFailedPanel order={order} auditEvents={auditEvents} />;
}
if (order.status === "transform_failed") {
  return <FailedPanel order={order} stage="transform" />;
}
if (order.status === "delivery_failed") {
  return <FailedPanel order={order} stage="delivery" />;
}
```

The existing `STATUS_STAGE` map already has entries for all three; the above gate fires before the full page render, not inside it.

### 5. Wire into `SpineReview.tsx`

The `isError || order === null` gate at line 895 already handles null/error. Add a branch **after** the null check (order is loaded but failed):

```tsx
if (order?.status === "failed") {
  return <ParseFailedPanel order={order} auditEvents={undefined} />;
}
```

(SpineReview doesn't fetch audit events; the panel handles the missing `auditEvents` by falling back to `order.errorMessage`.)

### 6. Detect-format result caching (`UploadWorkbench.tsx`)

In `handleUpload`, after `uploadedOrderId` is set and before the animation timers:

```tsx
if (detection) {
  sessionStorage.setItem(`detectResult:${uploadedOrderId}`, JSON.stringify(detection));
}
```

### 7. Supplier preselect on `/upload?supplierId=` (`UploadWorkbench.tsx`)

Add `useSearchParams` import (already available in Next.js 15). On mount (or in the existing supplier-validation `useEffect`), if `searchParams.get("supplierId")` is non-null and the value exists in the loaded `suppliers` list, call `setSupplierId(param)`.

---

## Success Criteria

- Upload a scanned PDF → order lands in `status=failed` → `GET /api/orders/{id}` returns `errorMessage: "This PDF looks scanned…"` and the audit log has a `ParseFailed` event with `error` in the payload.
- Upload a CSV with no recognised columns → same flow; `errorMessage` says "No line-table columns detected…".
- Frontend `/orders/{id}` for a failed parse: shows `ParseFailedPanel` with the error text, a format chip, and a working "Re-upload" CTA that navigates to `/upload?supplierId=…` with the correct supplier pre-selected.
- Frontend `/inbox/{id}` (SpineReview) for a failed parse: same panel instead of generic "Failed to load".
- `transform_failed`: `FailedPanel` with amber "Output generation failed" and "Back to review" link.
- `delivery_failed`: `FailedPanel` with red "Delivery to supplier failed" and a working "Retry delivery" button wired to `POST /api/orders/{id}/redeliver`.
- Backend test suite: ≥272 tests pass (new tests bring the count up).
- `bun run build` in `project-proculink` passes.

---

## Out of scope

- New DB migration (not needed — `AuditEvents` table exists).
- Transform-failure or delivery-failure human-readable messages from those job classes (only parse failures are in scope for this spec; those jobs do set status but the existing audit events for them already carry raw messages adequate for the panel).
- Playwright automated test (manual failed-parse upload is the verification method per the task brief).
