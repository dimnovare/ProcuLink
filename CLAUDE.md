# ProcuLink — Claude Code Project Memory

## What this project is

ProcuLink is a **B2B supplier-order bridge** for Baltic/Nordic distributors,
wholesalers, and manufacturers that receive orders in messy formats (CSV, XLSX)
and need them transformed into supplier-ready structured documents and delivered.

One-sentence value proposition:
**Upload a buyer order file → validate → resolve mappings → transform to
supplier format → deliver.**

---

## Repository layout

```
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\
├── CLAUDE.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev port :5223
├── ProcuLink.Core\                  ← Domain models, service interfaces
│   ├── Entities\
│   ├── Repositories\                ← interfaces
│   └── Services\                    ← interfaces + implementations
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2 storage
│   ├── Persistence\                 ← ProcuLinkDbContext, migrations
│   └── Repositories\                ← EF implementations
├── ProcuLink.Transform\             ← ⚡ ACTIVE IN PHASE 2: parsers + transform
│   ├── Parsing\                     ← CsvOrderParser, XlsxOrderParser
│   └── Output\                      ← XmlTransformService, CsvTransformService
├── ProcuLink.Worker\                ← Placeholder (Phase 3)
└── ProcuLink.Web\                   ← Vite + React 18 (bun, git submodule)
                                       GitHub: dimnovare/project-proculink
```

---

## Tool division of labour

| Task | Tool |
|---|---|
| New screens / layouts / UI polish | **Lovable** → git pull |
| Auth wiring, API calls, business logic in frontend | **Claude Code** |
| All .NET backend | **Claude Code** |

---

## Tech stack — final

| Layer | Choice |
|---|---|
| Frontend | Vite + React 18 + TypeScript + Tailwind + shadcn/ui |
| Package manager | **bun** — never npm or yarn |
| Auth | Clerk (frontend + backend JWT) |
| API | ASP.NET Core 8 — dev :5223 |
| ORM | EF Core 9 + Npgsql |
| Database | PostgreSQL (Neon.tech dev) |
| File storage | Cloudflare R2 (S3-compatible, AWSSDK.S3) |
| Background jobs | Hangfire — Phase 3 only, do not add yet |

---

## Phase status

| Phase | Status |
|---|---|
| Phase 0 — Prototype spike | ✅ Done |
| Phase 1 — Auth + Postgres + Tenancy | ✅ Done |
| **Phase 2 — Core loop** | 🚧 **CURRENT** |
| Phase 3 — Sellable MVP | ⏳ Pending |
| Phase 4 — Commercial | ⏳ Pending |

---

## What Phase 1 delivered (do not redo)

- ✅ Clerk JWT auth on all API endpoints
- ✅ `ICurrentTenantService` — extracts `orgId` from JWT
- ✅ EF Core + Postgres with all schema tables
- ✅ `EfOrderRepository`, `EfSupplierProfileRepository`, `EfItemMappingRepository`
- ✅ Rate limiting + 10 MB upload guard + filename sanitisation
- ✅ Scalar OpenAPI UI at `/scalar`
- ✅ Clerk auth in frontend (`ClerkProvider`, `SignedIn`/`SignedOut` guards)
- ✅ Auth headers on all `api-client.ts` calls
- ✅ `index.html` cleaned up — no Lovable metadata
- ✅ `/mappings` and `/settings` stub pages + sidebar links

---

## 🚧 Phase 2 — Core loop

**Goal:** A real end-to-end workflow a human can complete start to finish.
Upload CSV/XLSX → parse → auto-resolve known mappings → review unknowns → transform to XML/CSV → download.
Everything synchronous. No background jobs.

The full data flow:
```
[User] upload file
  → API saves raw file to R2 (source_file_key)
  → parse CSV/XLSX → canonical lines
  → auto-resolve each line against item_mappings table
  → save purchase_order + purchase_order_lines to DB
  → return order (status: pending_review OR ready)

[User] reviews unresolved lines, types supplier codes
  → POST /api/orders/{id}/resolve
  → lines updated, new mappings optionally saved
  → order status → ready (if all resolved)

[User] clicks Transform, picks format
  → POST /api/orders/{id}/transform?format=xml|csv
  → validate all lines resolved
  → build output document
  → save artifact to R2 (outbound_artifacts row)
  → order status → delivered

[User] clicks Download
  → GET /api/orders/{id}/artifacts/{artifactId}/download
  → signed R2 URL (15 min) → file opens in browser
```

---

### Group A — File storage infrastructure

**All in `ProcuLink.Infrastructure`**

- [ ] **A1.** Add `AWSSDK.S3` NuGet package to `ProcuLink.Infrastructure.csproj`
- [ ] **A2.** Create `IFileStorageService` in `ProcuLink.Core\Services\`:
  ```csharp
  Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct);
  Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct);
  Task DeleteAsync(string key, CancellationToken ct);
  ```
- [ ] **A3.** Create `R2StorageService` in `ProcuLink.Infrastructure\Storage\`:
  - Use `AmazonS3Client` with `ServiceURL` set to the R2 endpoint from config
  - `UploadAsync` → `PutObjectRequest`
  - `GetSignedDownloadUrlAsync` → `GetPreSignedUrlRequest` with `Expires = DateTime.UtcNow + expiry`
  - Key convention: `{orgId}/{orderId}/{filename}` for order files,
    `{orgId}/{orderId}/artifacts/{artifactId}.{ext}` for transform output
- [ ] **A4.** Register `R2StorageService` as `IFileStorageService` singleton in `Program.cs`
- [ ] **A5.** Add R2 config section to `appsettings.Development.json` (see env vars section)

---

### Group B — Parsing layer (activate `ProcuLink.Transform`)

**Port the parsing logic that is currently in the controller into proper classes.**

- [ ] **B1.** Create `ParsedOrderLine` record in `ProcuLink.Transform\Parsing\`:
  ```csharp
  record ParsedOrderLine(
      int LineNumber, string BuyerItemCode, string? Description,
      decimal Quantity, string? Unit, decimal? UnitPrice
  );
  ```
- [ ] **B2.** Create `ParsedOrder` record:
  ```csharp
  record ParsedOrder(
      string? PoNumber, DateTime? OrderDate, string? BuyerName,
      string? Currency, IReadOnlyList<ParsedOrderLine> Lines
  );
  ```
- [ ] **B3.** Create `IPurchaseOrderParser` interface:
  ```csharp
  Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct);
  bool CanParse(string fileExtension);
  ```
- [ ] **B4.** Create `CsvOrderParser : IPurchaseOrderParser` — extract CSV parsing
  from `PurchaseOrdersController`. Use `CsvHelper` if already referenced, else
  plain `StreamReader`. Handles: comma and semicolon delimiters, header row
  normalisation (lowercase, trim), `lineNumber`, `itemCode`/`buyerItemCode`,
  `supplierItemCode`, `description`, `quantity`, `unit`, `unitPrice`/`price`.
- [ ] **B5.** Create `XlsxOrderParser : IPurchaseOrderParser` — extract XLSX parsing
  from controller. Use `ClosedXML` or `EPPlus` (whichever is already referenced).
  Same column normalisation as CSV.
- [ ] **B6.** Create `OrderParserFactory` — takes `IEnumerable<IPurchaseOrderParser>`,
  selects by file extension (`.csv` → CsvOrderParser, `.xlsx` → XlsxOrderParser).
  Throws `UnsupportedFileFormatException` for anything else.
- [ ] **B7.** Register all parsers + factory in `Program.cs` DI.

---

### Group C — Order service

**In `ProcuLink.Core\Services\`**

- [ ] **C1.** Create `IOrderService`:
  ```csharp
  Task<Result<PurchaseOrder>> CreateFromFileAsync(
      Guid organisationId, Guid supplierId,
      Stream fileStream, string filename, string contentType,
      CancellationToken ct);

  Task<Result<PurchaseOrder>> GetByIdAsync(
      Guid organisationId, Guid orderId, CancellationToken ct);

  Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(
      Guid organisationId, CancellationToken ct);
  ```
- [ ] **C2.** Create `OrderService : IOrderService` implementation:
  - `CreateFromFileAsync`:
    1. Sanitise filename
    2. Upload raw file to R2 via `IFileStorageService` → get `sourceFileKey`
    3. Parse via `OrderParserFactory` → `ParsedOrder`
    4. For each line: call `IItemMappingService.ResolveAsync(orgId, supplierId, buyerItemCode)`
    5. Build `PurchaseOrder` entity + `PurchaseOrderLine` entities
       - `needs_review = true` where no mapping found
       - `confidence = 1.0` for exact matches, `0` for unresolved
    6. Set order `status`:
       - All resolved → `OrderStatus.Ready`
       - Any unresolved → `OrderStatus.PendingReview`
    7. Save via `IOrderRepository`
    8. Write `audit_event`: `entity_type=Order, action=Created`
    9. Return saved order

---

### Group D — Mapping service

**In `ProcuLink.Core\Services\`**

- [ ] **D1.** Create `IItemMappingService`:
  ```csharp
  Task<string?> ResolveAsync(Guid orgId, Guid supplierId, string buyerItemCode, CancellationToken ct);
  Task UpsertAsync(Guid orgId, Guid supplierId, string buyerItemCode,
                   string supplierItemCode, MappingSource source, CancellationToken ct);
  Task<IReadOnlyList<ItemMapping>> GetForSupplierAsync(Guid orgId, Guid supplierId, CancellationToken ct);
  Task DeleteAsync(Guid orgId, Guid mappingId, CancellationToken ct);
  ```
- [ ] **D2.** Implement `ItemMappingService` using `IItemMappingRepository`.
  `ResolveAsync`: exact match on `(orgId, supplierId, buyerItemCode)` — no fuzzy matching yet.
- [ ] **D3.** `MappingSource` enum: `Manual`, `Imported`, `Suggested`

---

### Group E — Resolve endpoint

**Thin up `PurchaseOrdersController`, call services.**

- [ ] **E1.** Add request DTO:
  ```csharp
  record ResolveOrderRequest(
      List<LineResolution> LineResolutions,
      bool SaveMappings
  );
  record LineResolution(int LineNumber, string SupplierItemCode);
  ```
- [ ] **E2.** Add `POST /api/orders/{id}/resolve` action:
  1. Load order — return 404 if not found or wrong org
  2. For each `LineResolution`: update `purchase_order_lines.supplier_item_code`,
     set `needs_review = false`
  3. If `SaveMappings: true` → call `IItemMappingService.UpsertAsync` for each line
     with `source = MappingSource.Manual`
  4. Recompute order status: if no lines have `needs_review = true` → `OrderStatus.Ready`
  5. Save changes
  6. Write audit event: `action=Resolved, payload={lineCount, savedMappings}`
  7. Return updated order DTO
- [ ] **E3.** Add `IOrderService.ResolveAsync` method and implement.

---

### Group F — Transform service (activate `ProcuLink.Transform`)

- [ ] **F1.** Create `ITransformService` in `ProcuLink.Core\Services\`:
  ```csharp
  Task<TransformResult> TransformAsync(
      PurchaseOrder order,
      IReadOnlyList<PurchaseOrderLine> lines,
      SupplierProfile profile,
      OutputFormat format,
      CancellationToken ct);
  ```
  ```csharp
  record TransformResult(Stream Content, string ContentType, string FileExtension);
  enum OutputFormat { Xml, Csv }
  ```
- [ ] **F2.** Create `XmlTransformService` in `ProcuLink.Transform\Output\`:
  Port the XML generation from `PurchaseOrdersController`. Output must include:
  supplier item codes, quantities, unit prices, PO number, order date.
  Validate: no line may have `needs_review = true` or null `supplier_item_code`.
  Throw `TransformValidationException` if validation fails.
- [ ] **F3.** Create `CsvTransformService` in `ProcuLink.Transform\Output\`:
  Same validation. Output columns: `SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal`.
- [ ] **F4.** Add `POST /api/orders/{id}/transform` endpoint:
  - Body: `{ "format": "xml" | "csv" }`
  - Load order + lines; return 422 if any line has `needs_review = true` with message
    "Resolve all lines before transforming"
  - Set order status → `OrderStatus.Transforming`
  - Call `ITransformService.TransformAsync`
  - Upload result stream to R2 → key: `{orgId}/{orderId}/artifacts/{newGuid}.{ext}`
  - Insert `outbound_artifacts` row
  - Set order status → `OrderStatus.Delivered` (for now; Phase 3 splits this further)
  - Write audit event: `action=Transformed, payload={format, artifactId, fileKey}`
  - Return `{ artifactId, format, createdAt }`
- [ ] **F5.** Register `XmlTransformService` and `CsvTransformService` in DI.

---

### Group G — Download endpoint

- [ ] **G1.** Add `GET /api/orders/{id}/artifacts/{artifactId}/download` endpoint:
  - Load `outbound_artifacts` row — 404 if not found or wrong org
  - Call `IFileStorageService.GetSignedDownloadUrlAsync(fileKey, TimeSpan.FromMinutes(15))`
  - Return `{ url, expiresAt }`
- [ ] **G2.** Do NOT return the file bytes directly — always redirect via signed URL.

---

### Group H — Slim down the controller

- [ ] **H1.** Refactor `PurchaseOrdersController`:
  - Upload action → delegates entirely to `IOrderService.CreateFromFileAsync`
  - Get/list actions → delegates to `IOrderService`
  - Remove all inline parsing, file I/O, and transform code from the controller
  - Controller file should be under 100 lines when done
- [ ] **H2.** Delete `FileOrderRepository` once `EfOrderRepository` is confirmed working.
  Do not keep dead code.

---

### Group I — Frontend: upload flow

**In `ProcuLink.Web\src`**

- [ ] **I1.** Update `api-client.ts` `uploadPurchaseOrder`:
  - Expect new response shape: `{ order: PurchaseOrder, validationMessages: string[] }`
  - After upload success, the caller should navigate to `/orders/{order.id}`
- [ ] **I2.** Update `UploadPage.tsx`:
  - On successful upload → `navigate(\`/orders/${result.order.id}\`)`
  - Show toast with validation messages if any warnings returned
  - Remove any inline order preview that was shown on the upload page

---

### Group J — Frontend: order detail

- [ ] **J1.** Update `OrderDetailPage.tsx` / `OrderLineTable.tsx`:
  - Add status badge at the top of the page using the order's `status` field
    - `pending_review` → amber badge "Needs Review"
    - `ready` → green badge "Ready to Transform"
    - `transforming` → blue badge "Transforming..."
    - `delivered` → green badge "Delivered"
    - `failed` → red badge "Failed"
  - Highlight rows where `needs_review === true` — amber background or warning icon
  - Show a count banner: "3 lines need attention" above the table when `status === pending_review`
- [ ] **J2.** Add `artifacts` field to the `PurchaseOrder` type in `types/procurement.ts`:
  ```ts
  artifacts?: { id: string; format: string; createdAt: string }[];
  ```
- [ ] **J3.** Update `realGetOrderById` to request artifact list (if API includes it in response).

---

### Group K — Frontend: resolve section

- [ ] **K1.** `ResolveSection.tsx` — wire to real endpoint:
  - Call `POST /api/orders/{id}/resolve` with auth header
  - Payload: `{ lineResolutions: [...], saveMappings: boolean }`
  - On success: call `queryClient.invalidateQueries(['order', id])` to refresh
  - Show success toast: "Mappings saved. Order is ready to transform."
- [ ] **K2.** "Save mapping for future orders" checkbox must send `saveMappings: true`
  in the request body — not just a UI toggle.
- [ ] **K3.** Disable the Resolve submit button if any resolution input is empty.

---

### Group L — Frontend: transform + download

- [ ] **L1.** `OrderActions.tsx` — Transform button:
  - Disable when `order.status !== 'ready'` — show tooltip "Resolve all lines first"
  - On click: open a small popover or dialog with format picker: **XML** | **CSV**
  - On format select: call `POST /api/orders/{id}/transform` with `{ format }`
  - Show loading spinner on the button during the request
  - On success: invalidate order query, show toast "Transform complete"
  - On error (422 — unresolved lines): show the error message from the API
- [ ] **L2.** `OrderActions.tsx` — Download button:
  - Show only when `order.artifacts?.length > 0`
  - On click: call `GET /api/orders/{id}/artifacts/{latestArtifact.id}/download`
  - Open the returned `url` in a new tab: `window.open(url, '_blank')`
  - If multiple artifacts exist, show a dropdown listing them by format + date
- [ ] **L3.** Remove ALL `console.log(...)` from `OrderActions.tsx` — no placeholders.

---

### Group M — Frontend: mappings page

- [ ] **M1.** `/mappings` page — build out the stub:
  - Supplier selector (dropdown) — loads supplier list from `/api/suppliers`
  - On supplier select: load mappings from `/api/suppliers/{supplierId}/mappings`
    using TanStack Query with `[supplierId]` as key
  - Show table: Buyer Item Code | Supplier Item Code | Source | Actions
  - Inline edit of `Supplier Item Code` — save on blur / Enter key
  - Delete row — calls `DELETE /api/suppliers/{supplierId}/mappings/{mappingId}`
  - Show empty state: "No mappings yet for this supplier"
- [ ] **M2.** Add `getSupplierMappings(supplierId)` and `deleteMappingById` to `api-client.ts`

---

### Group N — New API endpoints to add (summary)

These must all be added to match what the frontend expects:

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/orders/{id}/resolve` | Resolve lines + optionally save mappings |
| `POST` | `/api/orders/{id}/transform` | Transform to XML or CSV |
| `GET` | `/api/orders/{id}/artifacts/{artifactId}/download` | Signed download URL |
| `GET` | `/api/suppliers` | List supplier names for the org (for dropdowns) |
| `GET` | `/api/suppliers/{supplierId}/mappings` | All mappings for a supplier |
| `DELETE` | `/api/suppliers/{supplierId}/mappings/{mappingId}` | Delete a mapping |

All require `[Authorize]`. All must scope queries to `currentOrgId`.

---

### Phase 2 definition of done

Phase 2 is complete when a user can, without touching mock data or the filesystem:
1. Log in via Clerk
2. Upload a real CSV or XLSX order file
3. See the parsed order with unresolved lines highlighted
4. Type supplier codes for unresolved lines and save (with "save for future" working)
5. Click Transform, pick XML or CSV
6. Click Download, get the actual file
7. See the order status update correctly at each step

---

## Phase 3 — Sellable MVP (next)

- Hangfire background jobs: async parse, transform, and deliver pipeline
- Per-supplier webhook delivery with retries and dead-letter queue
- Delivery attempt tracking visible in order detail UI
- Audit trail timeline in order detail page
- Onboarding wizard: org setup → add first supplier → upload first order
- Deploy: Railway (API) + Vercel (frontend)
- Error monitoring: Sentry

## Phase 4 — Commercial

- Next.js marketing site (separate repo)
- Stripe billing + usage metering
- PDF and email ingestion
- ERP connectors: Erply, Directo (Estonia/Baltics first)
- AI mapping suggestions via Claude API for unrecognised item codes
- Peppol / Telema integration

---

## Coding conventions

### .NET
- Controllers: thin. Validate input → call service → return DTO.
- Services in `ProcuLink.Core\Services\`.
- Every service method takes `Guid organisationId` as first param.
- Use `Result<T>` for business errors — no exceptions for expected failures.
- All EF queries: `.Where(x => x.OrganisationId == organisationId)`.
- No raw SQL.
- `CancellationToken ct` on all async methods.

### Frontend
- TanStack Query for all server state. No `useEffect` for data fetching.
- All API calls via `src/lib/api-client.ts`. No direct `fetch` in components.
- No mock data. No `console.log` placeholders.
- `bun` for all package operations.
- Tailwind only — no inline styles.

---

## Environment variables

### `ProcuLink.Api\appsettings.Development.json`
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=...;Database=proculink_dev;Username=...;Password=..."
  },
  "Clerk": {
    "Authority": "https://<your-clerk-domain>.clerk.accounts.dev"
  },
  "Storage": {
    "R2AccountId": "",
    "R2AccessKeyId": "",
    "R2SecretAccessKey": "",
    "R2BucketName": "proculink-dev",
    "R2Endpoint": "https://<accountid>.r2.cloudflarestorage.com"
  }
}
```

### `ProcuLink.Web\.env` (committed)
```
VITE_API_BASE_URL=http://localhost:5223
VITE_USE_MOCK=false
```

### `ProcuLink.Web\.env.local` (gitignored)
```
VITE_CLERK_PUBLISHABLE_KEY=pk_test_...
```

---

## Key links
- Frontend GitHub: https://github.com/dimnovare/project-proculink
- API dev: http://localhost:5223
- Scalar UI: http://localhost:5223/scalar
- Neon.tech: https://neon.tech
- Clerk: https://clerk.com
- Cloudflare R2: https://dash.cloudflare.com

---

## What NOT to do
- ❌ `npm install` — use **bun**
- ❌ EF queries without `org_id` scope
- ❌ Hangfire or any background jobs — Phase 3 only
- ❌ AI/LLM calls — Phase 4 only
- ❌ PDF or email ingestion — Phase 4 only
- ❌ Billing or marketing pages — Phase 4 only
- ❌ Raw SQL — EF Core only
- ❌ Filesystem storage — R2 only
- ❌ `console.log` placeholders in production code
- ❌ Scaffold a new frontend — `ProcuLink.Web` is the frontend
