# ProcuLink — Claude Code Project Memory

## What this project is

ProcuLink is a **B2B supplier-order bridge** for Baltic/Nordic distributors,
wholesalers, and manufacturers that receive orders in messy formats (CSV, XLSX)
and need them transformed into supplier-ready structured documents and delivered.

**Upload a buyer order file → validate → resolve mappings → transform → deliver.**

---

## Repository layout

```
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\        ← .NET solution root
├── CLAUDE.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev port :5223
├── ProcuLink.Core\                  ← Domain models, interfaces
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2, service impls
├── ProcuLink.Transform\             ← Parsers (CSV/XLSX) + transform (XML/CSV)
├── ProcuLink.Worker\                ← ⚡ ACTIVATE IN PHASE 3 — Hangfire jobs
└── (no ProcuLink.Web — frontend is a separate repo, see below)

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\   ← Frontend repo
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun
```

---

## Tool division of labour

| Task | Tool |
|---|---|
| New screens / layouts / UI polish | **Lovable** → git pull in `project-proculink` |
| Auth wiring, API calls, data hooks, bug fixes | **Claude Code** in `project-proculink` |
| All .NET backend | **Claude Code** in `ProcuLink` solution |

---

## Tech stack — final

| Layer | Choice |
|---|---|
| Frontend | Vite + React 18 + TypeScript + Tailwind + shadcn/ui |
| Package manager | **bun** — never npm or yarn |
| Auth | Clerk (frontend `@clerk/clerk-react` + backend JWT) |
| API | ASP.NET Core 8 — dev :5223 |
| ORM | EF Core 9 + Npgsql |
| Database | PostgreSQL — `Host=localhost;Port=5435;Database=proculink_dev` |
| File storage | Cloudflare R2 (AWSSDK.S3) |
| Background jobs | Hangfire + Hangfire.PostgreSql — Phase 3 only |

---

## Phase status

| Phase | Status |
|---|---|
| Phase 0 — Prototype spike | ✅ Done |
| Phase 1 — Auth + Postgres + Tenancy | ✅ Done |
| Phase 2 — Core loop | ✅ Done |
| **Phase 3 — Sellable MVP** | 🚧 **CURRENT** |
| Phase 4 — Commercial | ⏳ Pending |

---

## What Phase 2 delivered (authoritative — do not redo)

### Backend
- ✅ `R2StorageService` — upload, signed URL, delete via AWSSDK.S3
- ✅ `CsvOrderParser`, `XlsxOrderParser`, `OrderParserFactory` in `ProcuLink.Transform\Parsing`
- ✅ `XmlTransformService`, `CsvTransformService` in `ProcuLink.Transform\Output`
- ✅ `OrderService` — full lifecycle: CreateFromFile, GetById, List, Resolve, Transform, GetDownloadUrl
- ✅ `ItemMappingService` — Resolve (exact match), Upsert, GetForSupplier, Delete
- ✅ `OrdersController` — thin, <100 lines, all endpoints org-scoped with [Authorize]
- ✅ `SuppliersController` — GET /api/suppliers, GET/DELETE /api/suppliers/{id}/mappings
- ✅ All EF entities mapped in `ProcuLinkDbContext`, migration `InitialSchema` applied
- ✅ Clerk JWT middleware, `ICurrentTenantService`, rate limiting, upload size guard, filename sanitisation

### Frontend (`project-proculink`)
- ✅ Clerk `ClerkProvider` in `main.tsx`, `SignedIn`/`SignedOut` guards in `App.tsx`
- ✅ `authHeader()` helper in `api-client.ts` — all real* functions attach Bearer token
- ✅ `index.html` — ProcuLink title/description, no Lovable metadata
- ✅ `package.json` name: `proculink-web`
- ✅ `OrderDetailPage` — status badge, unresolved banner, line table, artifacts list, download button
- ✅ `ResolveSection` — wired to `POST /api/orders/{id}/resolve`, saveMappings checkbox functional
- ✅ `OrderActions` — format picker (XML/CSV), Transform button wired, no console.log
- ✅ `UploadPage` — navigates to `/orders/{id}` after upload
- ✅ `MappingsPage` — supplier dropdown, mapping table with delete
- ✅ `vercel.json` present for frontend deployment

---

## Known tech debt to fix in Phase 3

- ⚠️ `OrderDetailPage`, `MappingsPage`, `UploadPage` use `useEffect` for data fetching
  — **must be converted to TanStack Query hooks**
- ⚠️ `SupplierProfilesController` still injects `ISupplierProfileRepository` (file-based)
  — **must be migrated to EF Core in Phase 3**
- ⚠️ `R2StorageService` throws `InvalidOperationException` on startup if credentials are empty
  — **add `LocalFileStorageService` (writes to `/tmp/proculink-dev/`) for dev when R2 not configured**
- ⚠️ Old Phase 0 types (`PurchaseOrder`, `PurchaseOrderSummary`, `AutomationStatus`) still in
  `types/procurement.ts` — dead code, remove when safe
- ⚠️ `lovable-tagger` still in devDependencies — harmless but remove it

---

## 🚧 Phase 3 — Sellable MVP

**Goal:** A pilot customer can sign up, configure a supplier, upload orders reliably,
and receive output — without any manual intervention from us. Everything must be
observable, retryable, and deployable.

---

### Group A — Fix tech debt first (do this before new features)

- [ ] **A1.** Add `LocalFileStorageService` to `ProcuLink.Infrastructure\Storage\`:
  - Implements `IFileStorageService`
  - Writes to `Path.Combine(Path.GetTempPath(), "proculink-dev", key)`
  - `GetSignedDownloadUrlAsync` returns a local API endpoint URL, not a signed URL
  - Register conditionally in `Program.cs`:
    ```csharp
    if (string.IsNullOrEmpty(config["Storage:R2AccessKeyId"]))
        builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
    else
        builder.Services.AddSingleton<IFileStorageService, R2StorageService>();
    ```
- [ ] **A2.** Add `GET /api/dev/files/{**key}` endpoint (dev-only, no auth) to serve local files
  — only registered when `app.Environment.IsDevelopment()`.
- [ ] **A3.** Convert `UploadPage.tsx` supplier loading to TanStack Query:
  ```ts
  const { data: suppliers = [] } = useQuery({
    queryKey: ['suppliers'],
    queryFn: () => apiClient.getSuppliers(),
  });
  ```
- [ ] **A4.** Convert `OrderDetailPage.tsx` order loading to TanStack Query:
  ```ts
  const { data: order, isLoading, refetch } = useQuery({
    queryKey: ['order', id],
    queryFn: () => apiClient.getOrderById(id!),
    enabled: !!id,
  });
  ```
  Replace `handleOrderUpdated` with `queryClient.invalidateQueries(['order', id])`.
- [ ] **A5.** Convert `MappingsPage.tsx` to TanStack Query:
  - `useQuery(['suppliers'])` for supplier list
  - `useQuery(['mappings', selectedId], ..., { enabled: !!selectedId })` for mappings
  - `useMutation` for delete
- [ ] **A6.** Migrate `SupplierProfilesController` to EF:
  - Remove `ISupplierProfileRepository` injection
  - Inject `ProcuLinkDbContext` directly (or create `EfSupplierProfileRepository`)
  - All queries: `.Where(x => x.OrgId == _tenant.OrganisationId)`
- [ ] **A7.** Remove dead types from `types/procurement.ts`:
  `PurchaseOrder`, `PurchaseOrderLine`, `PurchaseOrderSummary`, `AutomationStatus`
- [ ] **A8.** Remove `lovable-tagger` from devDependencies: `bun remove lovable-tagger`

---

### Group B — Supplier management (required for onboarding)

Currently there is no way to create a supplier. The upload page lists suppliers from the DB,
but the DB is empty for a new org. Fix this before onboarding wizard.

- [ ] **B1.** Add `POST /api/suppliers` — create a supplier for the org:
  ```json
  { "name": "Supplier Name" }
  ```
  Returns `{ id, name }`. Validates name is unique per org.
- [ ] **B2.** Add `PUT /api/suppliers/{id}` — rename a supplier.
- [ ] **B3.** Add `DELETE /api/suppliers/{id}` — soft-delete (set `deleted_at`, filter from all queries).
  Add `deleted_at timestamptz` column + EF migration.
- [ ] **B4.** Add `POST /api/suppliers/{id}/profiles` — create or update supplier profile for the org.
  Body: `{ outputFormat, destinationType, destinationConfig }`.
  This replaces the legacy `POST /api/supplier-profiles` endpoint.
- [ ] **B5.** Frontend: Add a Suppliers page (`/suppliers`) — list, create, edit name, delete.
  Each supplier shows its profile config if one exists.
  Wire to the new endpoints above. Use TanStack Query throughout.
- [ ] **B6.** Update `UploadPage.tsx` to show "Add supplier" link when the list is empty.

---

### Group C — Hangfire background jobs

Replace the synchronous in-request parse+transform with async jobs.
This is essential for reliability — large files should not block HTTP.

- [ ] **C1.** Add NuGet packages to `ProcuLink.Worker.csproj`:
  - `Hangfire.Core`
  - `Hangfire.PostgreSql`
  - `Hangfire.AspNetCore`
- [ ] **C2.** Add Hangfire to `ProcuLink.Api\Program.cs`:
  ```csharp
  builder.Services.AddHangfire(cfg => cfg
      .UsePostgreSqlStorage(connectionString));
  builder.Services.AddHangfireServer();
  app.UseHangfireDashboard("/hangfire"); // dev only
  ```
- [ ] **C3.** Create `ParseOrderJob` in `ProcuLink.Worker`:
  - Takes `orderId` and `organisationId`
  - Sets order status → `parsing` (add to enum)
  - Runs `OrderParserFactory` + item resolution
  - Updates order lines + status → `pending_review` or `ready`
  - On failure: status → `failed`, write audit event
- [ ] **C4.** Create `TransformOrderJob` in `ProcuLink.Worker`:
  - Takes `orderId`, `organisationId`, `format`
  - Runs transform + uploads artifact to R2
  - On success: status → `ready_to_deliver`
  - Enqueues `DeliverOrderJob` if supplier has a webhook configured
  - On failure: status → `transform_failed`
- [ ] **C5.** Create `DeliverOrderJob` in `ProcuLink.Worker`:
  - Takes `orderId`, `organisationId`, `artifactId`
  - Reads supplier profile `destination_config` for webhook URL and headers
  - POSTs the artifact content to the webhook endpoint
  - Writes `delivery_attempts` row with status + response_code
  - On 2xx: order status → `delivered`
  - On 4xx: status → `delivery_failed` (no retry — client error)
  - On 5xx or timeout: Hangfire automatic retry (3 attempts, exponential backoff)
  - After 3 failures: status → `delivery_failed`, write dead-letter audit event
- [ ] **C6.** Update `OrdersController.Upload` to enqueue `ParseOrderJob` instead of parsing inline.
  Return immediately with `{ orderId, status: "parsing" }`.
- [ ] **C7.** Update `OrdersController.Transform` to enqueue `TransformOrderJob` instead of running inline.
  Return immediately with `{ status: "transforming" }`.
- [ ] **C8.** Add `parsing` and `ready_to_deliver` and `transform_failed` and `delivery_failed`
  to the status string set. Update `OrderStatus` type in `types/procurement.ts` to match.
- [ ] **C9.** Add `GET /api/orders/{id}/status` — lightweight endpoint returning just `{ status }`.
  Used by the frontend to poll while an order is in `parsing` or `transforming` state.

---

### Group D — Frontend: async status polling

- [ ] **D1.** In `OrderDetailPage.tsx`: when `order.status` is `parsing` or `transforming`,
  poll `GET /api/orders/{id}/status` every 2 seconds using TanStack Query's `refetchInterval`.
  Stop polling when status changes to a stable state.
  Show a spinner with appropriate message ("Parsing file…" / "Transforming…").
- [ ] **D2.** Add `parsing`, `ready_to_deliver`, `transform_failed`, `delivery_failed` to
  `StatusBadge` component with appropriate colours and labels.
- [ ] **D3.** `UploadPage.tsx`: after upload, immediately navigate to order detail.
  The polling in D1 takes care of showing progress.
- [ ] **D4.** `OrderActions.tsx`: after clicking Transform, navigate to order detail.
  Remove the inline loading state (job is async now).

---

### Group E — Audit trail in UI

- [ ] **E1.** Add `GET /api/orders/{id}/audit` endpoint:
  Returns `audit_events` rows for this order, newest first:
  `[{ action, payload, createdAt }]`
- [ ] **E2.** Add `AuditTimeline` component in `src/components/orders/`:
  Vertical timeline of events — Created, Resolved, Transformed, Delivered, Failed.
  Show `createdAt` as relative time ("2 minutes ago") and absolute on hover.
- [ ] **E3.** Add `AuditTimeline` to `OrderDetailPage` sidebar, below the Summary card.
  Use TanStack Query: `useQuery(['order-audit', id], ...)`.

---

### Group F — Onboarding wizard

New users land on an empty dashboard with no suppliers and no orders.
The wizard walks them through setup the first time.

- [ ] **F1.** Add `GET /api/onboarding/status` endpoint:
  Returns `{ hasSupplier: bool, hasUpload: bool, hasDelivery: bool }` for the org.
- [ ] **F2.** Create `OnboardingWizard` component — 3-step modal or full page:
  - Step 1: Create your first supplier (name input → POST /api/suppliers)
  - Step 2: Upload your first order (embedded FileUploadZone)
  - Step 3: Done — "Your first order is processing"
- [ ] **F3.** Show `OnboardingWizard` on `Dashboard` page when `!hasSupplier`.
  Dismiss permanently once supplier + first upload are done.
- [ ] **F4.** `Dashboard` page: replace placeholder stats with real data:
  - Total orders this month
  - Orders pending review
  - Orders delivered
  - Pull from `GET /api/dashboard/stats` (add this endpoint)

---

### Group G — Deploy

- [ ] **G1.** Backend — deploy to Railway:
  - Create `railway.toml` in solution root
  - Set env vars in Railway dashboard: `ConnectionStrings__DefaultConnection`,
    `Clerk__Authority`, `Storage__R2*`
  - Add `ASPNETCORE_URLS=http://+:$PORT` to Railway config
- [ ] **G2.** Frontend — deploy to Vercel:
  - `vercel.json` already exists ✅
  - Set env vars in Vercel: `VITE_API_BASE_URL` (Railway URL), `VITE_CLERK_PUBLISHABLE_KEY`
  - Confirm `VITE_USE_MOCK=false` in Vercel env
- [ ] **G3.** Database — Neon.tech:
  - Create a production database project (keep dev separate)
  - Run `dotnet ef database update` against production connection string once
- [ ] **G4.** CORS: update `Program.cs` to allow the Vercel production domain
  alongside localhost origins.
- [ ] **G5.** Health check: `app.MapHealthChecks("/health")` — Railway uses this for deploy validation.
- [ ] **G6.** Add Sentry to the backend:
  - `dotnet add package Sentry.AspNetCore`
  - `builder.WebHost.UseSentry(dsn)` in `Program.cs`
- [ ] **G7.** Add Sentry to the frontend:
  - `bun add @sentry/react`
  - Init in `main.tsx` with DSN from `VITE_SENTRY_DSN`

---

### Phase 3 definition of done

Phase 3 is complete when:
1. A new user can sign up via Clerk, land on the dashboard, and be guided through setup
2. Upload → parse → resolve → transform → download works end-to-end without hitting the API synchronously
3. Webhook delivery is attempted with retries and failure state is visible
4. The audit trail is visible on every order
5. The app is deployed and accessible at a public URL
6. An error in either the API or frontend appears in Sentry

---

## Phase 4 — Commercial (after first paying customers)

- Next.js marketing site (separate repo — public SEO pages, pricing, sign-up CTA)
- Stripe billing + usage metering (orders processed per month)
- PDF ingestion (extract line items from PDF purchase orders via document parsing)
- Email polling (IMAP — receive order emails, attach to supplier)
- ERP connectors: Erply and Directo (Estonia/Baltics first)
- AI mapping suggestions: call Claude API for unrecognised buyer item codes
- Peppol / Telema e-invoicing integration
- Bulk mapping import (CSV upload of existing mapping tables)

---

## Coding conventions

### .NET backend
- Controllers: thin — validate → call service → return DTO.
- Services in `ProcuLink.Core\Services\` or `ProcuLink.Api\Services\`.
- Every service method: `Task<Result<T>> MethodAsync(Guid organisationId, ..., CancellationToken ct)`.
- `Result<T>` for business errors — no exceptions for expected failures.
- All EF queries: `.Where(x => x.OrganisationId == organisationId)` — mandatory, no exceptions.
- No raw SQL.
- Hangfire jobs are idempotent — safe to retry.

### Frontend (`project-proculink`)
- **TanStack Query for ALL server state** — no `useEffect` for data fetching. Ever.
- `useMutation` for writes (POST/PUT/DELETE).
- All API calls via `src/lib/api-client.ts`. No direct `fetch` in components.
- No mock data after Phase 1.
- `bun` for all package operations.
- Tailwind only — no inline styles.

---

## Environment variables

### `ProcuLink.Api\appsettings.Development.json`
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5435;Database=proculink_dev;Username=postgres;Password=postgres"
  },
  "Clerk": {
    "Authority": "https://golden-alpaca-43.clerk.accounts.dev"
  },
  "Storage": {
    "R2AccountId": "",
    "R2AccessKeyId": "",
    "R2SecretAccessKey": "",
    "R2BucketName": "proculink-dev",
    "R2Endpoint": "https://<accountid>.r2.cloudflarestorage.com"
  },
  "Hangfire": {
    "ConnectionString": "Host=localhost;Port=5435;Database=proculink_dev;Username=postgres;Password=postgres"
  }
}
```
Note: If `Storage:R2AccessKeyId` is empty, `LocalFileStorageService` is used automatically.

### `project-proculink\.env` (committed)
```
VITE_API_BASE_URL=http://localhost:5223
VITE_USE_MOCK=false
```

### `project-proculink\.env.local` (gitignored — Clerk key goes here)
```
VITE_CLERK_PUBLISHABLE_KEY=pk_test_...
VITE_SENTRY_DSN=          ← add when Sentry is set up
```

---

## Key links
- Frontend GitHub: https://github.com/dimnovare/project-proculink
- Frontend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
- Backend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
- API dev: http://localhost:5223
- Scalar UI: http://localhost:5223/scalar
- Hangfire dashboard (dev): http://localhost:5223/hangfire
- Clerk dashboard: https://clerk.com (authority: golden-alpaca-43.clerk.accounts.dev)
- Neon.tech: https://neon.tech
- Cloudflare R2: https://dash.cloudflare.com
- Railway: https://railway.app
- Vercel: https://vercel.com

---

## What NOT to do
- ❌ `npm install` — use **bun**
- ❌ EF queries without `org_id` scope — ever
- ❌ `useEffect` for data fetching — TanStack Query only
- ❌ Direct `fetch` in components — use `api-client.ts`
- ❌ Hangfire jobs that are not idempotent
- ❌ AI/LLM calls — Phase 4 only
- ❌ PDF or email ingestion — Phase 4 only
- ❌ Billing or marketing pages — Phase 4 only
- ❌ Raw SQL — EF Core only
- ❌ Filesystem storage for new code — R2 or LocalFileStorageService only
- ❌ Add Next.js — Phase 4 marketing site only
- ❌ Scaffold a new frontend — `project-proculink` is the frontend
