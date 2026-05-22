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
├── CLAUDE.md                        ← you are here
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — runs on :5223 in dev
├── ProcuLink.Core\                  ← Domain models
├── ProcuLink.Infrastructure\        ← File-based repos → replace with EF Core
├── ProcuLink.Worker\                ← Placeholder (activate Phase 3)
├── ProcuLink.Transform\             ← Placeholder (activate Phase 2)
└── ProcuLink.Web\                   ← Vite + React 18 frontend (git submodule)
                                       GitHub: dimnovare/project-proculink
                                       Package manager: bun
```

---

## Tool division of labour

| Task | Tool |
|---|---|
| New screens / page layouts / UI components | **Lovable** (pushes to GitHub → `git pull` in `ProcuLink.Web`) |
| Wiring auth, fixing API calls, business logic in frontend | **Claude Code** |
| All .NET backend code | **Claude Code** |

---

## Tech stack — final

| Layer | Choice |
|---|---|
| Frontend | Vite + React 18 + TypeScript + Tailwind + shadcn/ui |
| Package manager | **bun** (not npm, not yarn) |
| Frontend routing | react-router-dom v6 |
| Frontend data | TanStack Query v5 (`@tanstack/react-query`) |
| Frontend forms | react-hook-form + zod |
| Auth | **Clerk** — to be added |
| API | ASP.NET Core 8 — dev port **:5223** |
| ORM | EF Core 9 + Npgsql (replacing file repos) |
| Database | PostgreSQL (Neon.tech for dev) |
| File storage | Cloudflare R2 |
| Background jobs | Hangfire (Phase 3 only) |

---

## Current state — what is actually true right now

### Frontend (`ProcuLink.Web`) — better than expected

**Already done:**
- ✅ `VITE_USE_MOCK=false` is set in `.env` — real API mode is active
- ✅ Every endpoint has a real implementation in `api-client.ts`:
  `realGetOrders`, `realUploadPurchaseOrder`, `realResolvePurchaseOrder`,
  `realGetSupplierProfiles`, `realGetSupplierMappings`, etc.
- ✅ TanStack Query v5, react-hook-form, zod, shadcn/ui all installed
- ✅ All routes exist: `/`, `/upload`, `/orders`, `/orders/:id`, `/admin/suppliers`
- ✅ Integrated into .NET solution as a referenced project

**Still broken — fix these:**
- ❌ `index.html` title is "Lovable App", author is "Lovable", OG image is Lovable's
- ❌ No Clerk — all routes are public, no auth headers on API calls
- ❌ `package.json` name is `vite_react_shadcn_ts` — should be `proculink-web`
- ❌ `OrderActions.tsx` Transform and Send buttons are still `console.log` placeholders
- ❌ No `/mappings` page, no `/settings` page in the router

### Backend (`ProcuLink.Api`) — needs restructuring

**Already done:**
- ✅ CSV and XLSX upload → parsed to canonical `PurchaseOrder`
- ✅ Supplier profiles via `FileSupplierProfileRepository`
- ✅ Item mapping resolution via `FileItemMappingRepository`
- ✅ XML and CSV transform output
- ✅ Swagger UI

**Still broken — fix these:**
- ❌ No auth — all endpoints are public
- ❌ No tenancy — all data is global
- ❌ Filesystem is the database (`FileOrderRepository`, `FileItemMappingRepository`, etc.)
- ❌ Business logic crammed into one large controller
- ❌ No rate limiting, no upload size guard, no filename sanitisation
- ❌ No audit trail

---

## Database schema (implement in Phase 1)

```sql
-- Run in this order

organisations (id uuid PK, clerk_org_id text UNIQUE, name text, plan text, created_at)
users (id uuid PK, clerk_user_id text UNIQUE, email text, created_at)
memberships (id uuid PK, org_id FK, user_id FK, role text, created_at)

suppliers (id uuid PK, org_id FK, name text, created_at)
supplier_profiles (
  id uuid PK, supplier_id FK, org_id FK,
  accepted_formats text[],         -- 'csv' | 'xml' only
  required_fields jsonb,
  output_format text,
  destination_type text,           -- 'webhook' | 'download'
  destination_config jsonb,
  created_at, updated_at
)

purchase_orders (
  id uuid PK, org_id FK, supplier_id FK,
  po_number text, order_date date, currency text,
  status text,  -- pending_parse|pending_review|ready|transforming|delivered|failed
  source_file_key text,
  canonical_json jsonb,
  created_at, updated_at
)
purchase_order_lines (
  id uuid PK, order_id FK,
  line_number int, buyer_item_code text, supplier_item_code text,
  description text, quantity numeric, unit text, unit_price numeric,
  confidence float, needs_review bool
)

item_mappings (
  id uuid PK, org_id FK, supplier_id FK,
  buyer_item_code text, supplier_item_code text,
  confidence float, source text,  -- manual|imported|suggested
  created_at, updated_at,
  UNIQUE (org_id, supplier_id, buyer_item_code)
)

outbound_artifacts (id uuid PK, order_id FK, org_id FK, format text, file_key text, created_at)
delivery_attempts (
  id uuid PK, order_id FK, org_id FK,
  channel text, destination text, status text,
  attempted_at timestamptz, response_code int, error_message text
)

audit_events (
  id uuid PK, org_id FK, user_id FK,
  entity_type text, entity_id uuid,
  action text, payload jsonb, created_at timestamptz
)
```

**Rule without exception:** every EF query must include
`.Where(x => x.OrganisationId == currentOrgId)`.

---

## Phase plan

### ✅ Phase 0 — Done
Prototype spike. File-based backend + Lovable frontend. Loop proven.

---

### 🚧 Phase 1 — Foundation (CURRENT)
Real auth, real database, real tenancy. No new features.

**Backend tasks — do in this order:**
1. Add EF Core 9 + Npgsql to `ProcuLink.Infrastructure.csproj`
2. Create `ProcuLinkDbContext` with all entities above
3. `dotnet ef migrations add InitialSchema --project ProcuLink.Infrastructure --startup-project ProcuLink.Api`
4. `dotnet ef database update`
5. Add Clerk JWT middleware to `Program.cs`
6. Add `ICurrentTenantService` — extracts `orgId` from the JWT `org_id` claim
7. Add `[Authorize]` to all controllers
8. Replace `FileOrderRepository` → `EfOrderRepository`
9. Replace `FileSupplierProfileRepository` → `EfSupplierProfileRepository`
10. Replace `FileItemMappingRepository` → `EfItemMappingRepository`
11. Add rate limiting (20 req/min on upload endpoint)
12. Add 10 MB upload size limit
13. Add filename sanitisation to all upload paths
14. Add Scalar OpenAPI UI (`app.MapScalarApiReference()`)

**Frontend tasks — Claude Code in `ProcuLink.Web`:**
15. Fix `index.html` — replace all Lovable metadata:
    - title → "ProcuLink"
    - description → "Supplier order automation for Baltic distributors"
    - Remove all Lovable OG/twitter tags
16. Fix `package.json` name → `proculink-web`
17. `bun add @clerk/clerk-react`
18. Wrap `main.tsx` in `<ClerkProvider publishableKey={import.meta.env.VITE_CLERK_PUBLISHABLE_KEY}>`
19. Add `<SignedIn>/<SignedOut>` guards in `App.tsx` — redirect unauth to Clerk sign-in
20. Update `api-client.ts` — add `Authorization: Bearer <token>` header to all real* functions
    using `window.Clerk?.session?.getToken()`
21. Add `.env.local` (gitignored) with Clerk keys — do NOT put Clerk keys in `.env`

**Frontend tasks — Lovable:**
22. Add `/mappings` route + stub page, update sidebar
23. Add `/settings` route + stub page, update sidebar

Phase 1 is done when: login is required, data persists to Postgres, no mock data.

---

### Phase 2 — Core loop
Real upload → parse → resolve → transform → download. Synchronous, no jobs yet.

- Store uploaded files to R2
- Port parser from controller into `ProcuLink.Transform`
- Mapping engine against `item_mappings` table
- Review UI: inline resolve + "save mapping"
- Transform to XML/CSV, store artifact to R2
- Signed R2 download URL endpoint
- Wire `OrderActions.tsx` Transform button to real endpoint
- Remove `console.log` placeholders

---

### Phase 3 — Sellable MVP
- Hangfire jobs: parse, transform, deliver
- Per-supplier webhook with retries + dead-letter
- Audit trail in order detail UI
- Onboarding wizard
- Deploy: Railway (API) + Vercel (frontend)

---

### Phase 4 — Commercial
- Next.js marketing site (separate)
- Stripe billing
- PDF/email ingestion
- ERP connectors (Erply, Directo)
- AI mapping suggestions via Claude API
- Peppol / Telema

---

## Coding conventions

### .NET backend
- Controllers: thin — validate → call service → return DTO. No logic.
- Services in `ProcuLink.Core\Services\` — create this folder.
- Every service method: `Task<Result<T>> MethodAsync(Guid organisationId, ..., CancellationToken ct)`
- `Result<T>` pattern for business errors — no exceptions for expected failures.
- Repository interfaces in `ProcuLink.Core\Repositories\`.
- EF implementations in `ProcuLink.Infrastructure\Repositories\`.
- All EF queries: `.Where(x => x.OrganisationId == organisationId)` — mandatory.
- No raw SQL.

### Frontend
- No `useEffect` for data fetching — TanStack Query only.
- No direct `fetch` in components — all calls via `src/lib/api-client.ts`.
- No inline styles — Tailwind only.
- No mock data after Phase 1.
- Use `bun` for all package operations, not npm.

---

## Environment variables

### `ProcuLink.Api\appsettings.Development.json`
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=proculink_dev;Username=postgres;Password=postgres"
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

### `ProcuLink.Web\.env` (already exists, committed)
```
VITE_API_BASE_URL=http://localhost:5223
VITE_USE_MOCK=false
```

### `ProcuLink.Web\.env.local` (create this, gitignored — Clerk keys go here)
```
VITE_CLERK_PUBLISHABLE_KEY=pk_test_...
```

---

## Key links
- Frontend GitHub: https://github.com/dimnovare/project-proculink
- API dev: http://localhost:5223
- Frontend dev: http://localhost:5173 (Vite default) or check `vite.config.ts`
- Scalar UI (after setup): http://localhost:5223/scalar
- Neon.tech: https://neon.tech
- Clerk dashboard: https://clerk.com
- Cloudflare R2: https://dash.cloudflare.com

## What NOT to do
- ❌ Do not run `npm install` — this project uses **bun**
- ❌ Do not put Clerk secret keys in `.env` (only in `.env.local`, gitignored)
- ❌ Do not write EF queries without `org_id` scoping
- ❌ Do not add features until Phase 1 is complete
- ❌ Do not use the filesystem for any new storage
- ❌ Do not scaffold a new frontend — `ProcuLink.Web` is the frontend
- ❌ Do not add Next.js until Phase 4 marketing site work
- ❌ Do not call Claude API or any LLM in Phase 1 or 2
