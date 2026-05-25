# ProcuLink — Claude Code Project Memory

## What this project is

ProcuLink is a **B2B outbound procurement bridge** for buyer/procurement teams
that need to send purchase orders to many suppliers in each supplier's required
format and delivery channel.

**Import a buyer-side order source → validate → resolve supplier mappings → transform → deliver to the supplier.**

**First ICP / next-6-week wedge:** buyer/procurement teams sending POs out.
They have many supplier requirements, delivery channels, item-code mappings,
and acceptance errors. Keep the broader platform vision, but prioritize reliable
outbound PO processing before invoices, PEPPOL, broad document automation, or
large ERP connector coverage.

**Core workflow:** `Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn`.

**Current source of truth:** read `STATUS.md` before planning. It overrides stale
phase text in this file if there is a mismatch.

---

## Repository layout

```
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\          ← .NET solution
├── CLAUDE.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev :5223
├── ProcuLink.Core\                  ← Domain models + service interfaces
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2/Local storage
├── ProcuLink.Transform\             ← CSV/XLSX parsers + XML/CSV transform
└── ProcuLink.Worker\                ← Hangfire jobs host (Phase 3+)

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\  ← Frontend
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun
Framework: Next.js 15 (App Router) — see migration section below
```

---

## Claude Code plugins — install these, use them every session

These are real plugins with exact install commands sourced from their repos.
Install once globally. They persist across all sessions.

---

### 1. Superpowers — structured workflow framework
**Source:** https://github.com/obra/superpowers
**Install (inside a Claude Code session):**
```
/plugin marketplace add obra/superpowers-marketplace
/plugin install superpowers@superpowers-marketplace
```
Quit and restart Claude Code after install. Skills auto-inject on session start.

**What it does:** Forces Claude to plan before coding, write tests first, and
self-review before handing back. Prevents jumping straight to code on complex tasks.

**Slash commands:**
- `/superpowers:brainstorm` — explore requirements and design before any implementation.
  Claude asks questions instead of writing code immediately.
- `/superpowers:write-plan` — produce a written plan with 2–5 min tasks, exact file paths,
  and complete code. Required before `/superpowers:execute-plan`.
- `/superpowers:execute-plan` — runs subagents to implement the plan, with two-stage code
  review after each task.
- `/superpowers:debug` — systematic root cause investigation with hypothesis testing.
  Triggers architectural review after 3 failed fix attempts.
- `/superpowers:code-review` — reviews implementation against the plan and coding standards.

**When to invoke:**
- Any feature touching ≥3 files: run `/superpowers:brainstorm` first
- Any medium+ task: run `/superpowers:write-plan` then `/superpowers:execute-plan`
- Any bug that survives one fix attempt: run `/superpowers:debug`
- End of each task group: run `/superpowers:code-review`

---

### 2. frontend-design — production-grade UI
**Source:** https://github.com/anthropics/claude-code/tree/main/plugins/frontend-design
**Install:**
```
/plugin install frontend-design@claude-plugins-official
```
Or via npx: `npx claude-plugins install @anthropics/claude-code-plugins/frontend-design`

**What it does:** Provides production-grade UI judgement, layout critique, and
polish. For ProcuLink it must execute the locked local design system, not invent
a new visual direction.

**When to invoke explicitly:** Any new page, component, or significant UI change
in `project-proculink`. If Claude is about to generate a component, say
"use /frontend-design guidance for this" to make it explicit.

**Design source of truth:** `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\docs\design-system`.
Read `00-agent-quick-brief.md` first, then only the specific design files needed
for the current task. The locked direction is Direction 4 - The Bridge Layer,
supported by Direction 3 - System Identity.

---

### 3. code-review — automated PR review
**Source:** https://github.com/anthropics/claude-code/blob/main/plugins/code-review/README.md
**Install:**
```
/plugin install code-review@claude-plugins-official
```
Or via npx: `npx claude-plugins install @anthropics/claude-code-plugins/code-review`

**What it does:** Runs 5 parallel Sonnet agents checking: CLAUDE.md compliance,
bug detection, historical context, PR history, and code comments. Confidence-based
scoring filters false positives.

**Command:** `/code-review`

**When to invoke:** Run `/code-review` at the end of every task group (A, B, C…)
before marking it complete. Never skip. Paste the diff or describe what changed.

---

### 4. claude-mem — persistent session memory
**Source:** https://github.com/thedotmack/claude-mem
**Install:**
```
/plugin marketplace add thedotmack/claude-mem
/plugin install claude-mem
```
Then restart Claude Code.

**⚠️ Critical:** Do NOT run `npm install -g claude-mem` — that installs only the
SDK library without the hooks or worker service. Always use `/plugin` commands above.
Requires Node.js 18+.

**What it does:** Captures everything Claude does during sessions (tool calls,
decisions, file changes), compresses with AI, and injects relevant context into
future sessions via SQLite + vector search. Sessions no longer start cold.

**How it works:** Fully automatic after install — no manual commands needed.
Context injects at session start. To search past sessions explicitly, ask:
"What did we do with the order parsing last time?" or
"What bugs did we fix in the transform pipeline?"

**Optional manual:** After a major session, ask Claude to summarise key decisions
so claude-mem captures them with higher importance.

---

## Tool division of labour

| Task | Tool |
|---|---|
| Complex feature planning (≥3 files) | **`/superpowers:brainstorm`** first |
| Implementation from a plan | **`/superpowers:execute-plan`** |
| New pages / major UI components | Local design system + **`/frontend-design`** + Claude Code |
| UI component generation | Claude Code only, using `docs/design-system` |
| End of each task group | **`/code-review`** before marking done |
| All .NET backend | **Claude Code** in `ProcuLink` solution |
| Next.js routing, Server Components, middleware | **Claude Code** only |

**Lovable is not used for ProcuLink.** Do not ask for Lovable, do not import
Lovable-generated Vite/React code, and do not treat Lovable as a component
source. All UI/UX and design decisions run through the local design system,
`/frontend-design`, and Claude Design/reference images.

---

## Tech stack — final

| Layer | Choice |
|---|---|
| Frontend | **Next.js 15 (App Router)** + TypeScript + Tailwind + shadcn/ui |
| Package manager | **bun** — never npm or yarn |
| Auth | **`@clerk/nextjs`** (NOT `@clerk/clerk-react` — different package for Next.js) |
| Clerk middleware | `middleware.ts` in project root — protects `(app)` routes |
| Frontend data | TanStack Query v5 — in Client Components only |
| API | ASP.NET Core 8 — dev :5223 |
| ORM | EF Core 8 + Npgsql |
| Database | PostgreSQL — `Host=localhost;Port=5435;Database=proculink_dev` |
| File storage | Cloudflare R2 (dev: `LocalFileStorageService` when keys absent) |
| Background jobs | Hangfire + Hangfire.PostgreSql |
| Error tracking | Sentry (backend `Sentry.AspNetCore`, frontend `@sentry/nextjs`) |
| Deployment | Railway (API) + Vercel (frontend — native Next.js support) |

---

## Phase status

| Phase | Status |
|---|---|
| Phase 0 — Prototype spike | ✅ Done |
| Phase 1 — Auth + Postgres + Tenancy | ✅ Done |
| Phase 2 — Core loop | ✅ Done |
| Phase 3 — Sellable MVP | ✅ Done |
| Next.js migration | ✅ Done |
| Phase 4 — Commercial | 🚧 In progress — Group E AI mapping suggestions next |

---

## Latest committed implementation state (May 25 2026)

Read this before starting new work:

- **Group C2 billing reconciliation is implemented** in backend and frontend.
  - Backend commit: `18feb71 feat: reconcile final billing model backend`
  - Frontend commit: `6116af9 feat: reconcile final billing model frontend`
  - Status commit: `f957f16 docs: update billing reconciliation status`
- **Group D2 buyer-side supplier delivery config is implemented for the HTTP-first path.**
  - Key backend commits include `70f20bd`, `301b836`, `779a128`, `61832f7`, `09b36c3`, `ee20fbe`, `19c8e98`, `326529d`, `2d765fc`
  - Frontend commits: `7772f4a`, `748c6de`
- **Do not redo C2 or D2.** Treat both as implemented unless `STATUS.md` says a regression reopened them.
- **Manual/live QA still recommended:**
  - Stripe Checkout + Portal + webhook mapping with real Stripe test events.
  - HTTP delivery config test-fire against a running API session.
- **Last verified commands:**
  - `dotnet build ProcuLink.slnx --no-restore` passed.
  - `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore` passed, 25 tests.
  - `dotnet test ProcuLink.Transform.Tests\ProcuLink.Transform.Tests.csproj --no-restore` passed, 22 tests.
  - `bun run build` in `project-proculink` passed; existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

Next implementation group is **Group E — AI mapping suggestions**, but before coding:

1. Read `STATUS.md`.
2. Read only task-relevant design docs, starting with `docs/design-system/00-agent-quick-brief.md` if frontend is touched.
3. Use `/superpowers:brainstorm` and `/superpowers:write-plan` because Group E touches backend services, parsing flow, API contracts, and frontend review UI.

---

## Historical Next.js migration plan — complete, do not execute

**Status:** Complete. This section is kept only as historical context. Do **not**
run M1-M17 again.

**Approach:** In-place migration of `project-proculink`. Same GitHub repo, same
Vercel project. No new repo needed. Migrate route by route.

**Why same repo works:** Next.js is a framework switch, not a new project. All
existing components, API client, TanStack Query hooks, and shadcn/ui are reusable.
Vercel natively detects Next.js — just update `vercel.json` and redeploy.

### Target route structure (App Router)

```
project-proculink/
├── app/
│   ├── layout.tsx                  ← root layout: ClerkProvider, TanStack, Toaster
│   ├── (marketing)/                ← public routes — no auth, SSR/SSG
│   │   ├── layout.tsx              ← marketing layout (nav, footer)
│   │   ├── page.tsx                ← / landing page
│   │   ├── pricing/page.tsx
│   │   └── how-it-works/page.tsx
│   └── (app)/                      ← auth-protected routes
│       ├── layout.tsx              ← AppLayout with sidebar (Client Component)
│       ├── dashboard/page.tsx
│       ├── upload/page.tsx
│       ├── orders/
│       │   ├── page.tsx
│       │   └── [id]/page.tsx
│       ├── suppliers/page.tsx
│       ├── mappings/page.tsx
│       └── settings/page.tsx
├── middleware.ts                    ← Clerk auth guard for (app) routes
├── src/
│   ├── components/                 ← existing components (mostly unchanged)
│   ├── lib/api-client.ts           ← update VITE_ → NEXT_PUBLIC_ env vars
│   └── types/procurement.ts        ← unchanged
├── next.config.ts
└── vercel.json                     ← update for Next.js
```

### Migration task list — do in this exact order

**Use `/superpowers:write-plan` before starting this group.**

- [ ] **M1.** In `project-proculink`: scaffold Next.js 15 alongside existing files:
  ```bash
  bunx create-next-app@latest . --typescript --tailwind --app --src-dir no --import-alias "@/*" --yes
  ```
  This overwrites `package.json` — afterwards restore bun lockfile:
  `bun install`

- [ ] **M2.** Replace Clerk package:
  ```bash
  bun remove @clerk/clerk-react
  bun add @clerk/nextjs
  ```
  Note: `@clerk/nextjs` has different import paths than `@clerk/clerk-react`.

- [ ] **M3.** Replace Sentry package:
  ```bash
  bun remove @sentry/react
  bun add @sentry/nextjs
  ```

- [ ] **M4.** Create `middleware.ts` in project root:
  ```ts
  import { clerkMiddleware, createRouteMatcher } from '@clerk/nextjs/server';

  const isProtectedRoute = createRouteMatcher([
    '/dashboard(.*)', '/orders(.*)', '/upload(.*)',
    '/suppliers(.*)', '/mappings(.*)', '/settings(.*)',
  ]);

  export default clerkMiddleware((auth, req) => {
    if (isProtectedRoute(req)) auth().protect();
  });

  export const config = {
    matcher: ['/((?!_next|.*\\..*).*)'],
  };
  ```

- [ ] **M5.** Create `app/layout.tsx` — root layout:
  ```tsx
  import { ClerkProvider } from '@clerk/nextjs';
  import { Inter } from 'next/font/google';
  import './globals.css';

  export default function RootLayout({ children }) {
    return (
      <html lang="en">
        <body>
          <ClerkProvider>{children}</ClerkProvider>
        </body>
      </html>
    );
  }
  ```

- [ ] **M6.** Create `app/(app)/layout.tsx` — app shell:
  - Move `AppLayout.tsx` content here
  - Wrap with `QueryClientProvider` (must be a Client Component: `'use client'`)
  - Include `<Toaster />` and `<Sonner />`

- [ ] **M7.** Migrate pages one by one — copy from `src/pages/` → `app/(app)/*/page.tsx`:
  - `Dashboard.tsx` → `app/(app)/dashboard/page.tsx`
  - `OrdersPage.tsx` → `app/(app)/orders/page.tsx`
  - `OrderDetailPage.tsx` → `app/(app)/orders/[id]/page.tsx`
  - `UploadPage.tsx` → `app/(app)/upload/page.tsx`
  - `SuppliersPage.tsx` → `app/(app)/suppliers/page.tsx`
  - `MappingsPage.tsx` → `app/(app)/mappings/page.tsx`
  - Add `'use client'` at top of each (they use hooks/TanStack Query)

- [ ] **M8.** Replace all `react-router-dom` imports:
  - `useNavigate()` → `useRouter()` from `next/navigation`
  - `useParams()` → `useParams()` from `next/navigation` (same name, different import)
  - `<Link to="...">` → `<Link href="...">` from `next/link`
  - `<BrowserRouter>`, `<Routes>`, `<Route>` → delete entirely (App Router handles routing)
  - `bun remove react-router-dom`

- [ ] **M9.** Update env variable names in `api-client.ts` and all components:
  - `import.meta.env.VITE_API_BASE_URL` → `process.env.NEXT_PUBLIC_API_BASE_URL`
  - `import.meta.env.VITE_USE_MOCK` → `process.env.NEXT_PUBLIC_USE_MOCK`
  - `import.meta.env.VITE_CLERK_PUBLISHABLE_KEY` → handled by `@clerk/nextjs` automatically
    via `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY`

- [ ] **M10.** Update `.env` (committed):
  ```
  NEXT_PUBLIC_API_BASE_URL=http://localhost:5223
  NEXT_PUBLIC_USE_MOCK=false
  ```
  Update `.env.local` (gitignored):
  ```
  NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY=pk_test_...
  NEXT_PUBLIC_SENTRY_DSN=
  ```

- [ ] **M11.** Update `vercel.json` for Next.js:
  ```json
  {
    "framework": "nextjs"
  }
  ```
  Remove any Vite-specific SPA rewrites.

- [ ] **M12.** Create `next.config.ts`:
  ```ts
  import type { NextConfig } from 'next';
  const config: NextConfig = {
    // API is on a different origin — no rewrites needed
  };
  export default config;
  ```

- [ ] **M13.** Delete Vite artefacts:
  - `vite.config.ts`, `index.html`, `src/vite-env.d.ts`
  - `src/App.tsx`, `src/App.css`, `src/main.tsx` (replaced by App Router)
  - `bun remove vite @vitejs/plugin-react-swc`

- [ ] **M14.** Run `bun run build` — fix any TypeScript or import errors. Ship when green.

- [ ] **M15.** Create the `(marketing)` layout and stub pages:
  - `app/(marketing)/layout.tsx` — minimal layout, no auth, no sidebar
  - `app/(marketing)/page.tsx` — landing page stub with `<head>` metadata
  - `app/(marketing)/pricing/page.tsx` — pricing stub
  - `app/(marketing)/how-it-works/page.tsx` — how-it-works stub
  - These render as SSR pages — `generateMetadata()` for SEO

- [ ] **M16.** Redirect `/` to `/dashboard` if logged in:
  Update `middleware.ts` to redirect authenticated users away from marketing routes.

- [ ] **M17.** Run `/code-review` on the entire migration diff before merging.

---

## Phase 4 — Commercial (after migration)

### Group A — Remaining tech debt
**Status:** ✅ Done. Do not repeat unless `STATUS.md` says a regression reopened it.

- [ ] **A1.** `bun remove lovable-tagger`
- [ ] **A2.** Remove `SupplierProfilesController.cs` (redundant — routes now in `SuppliersController`)
- [ ] **A3.** Remove `ProcuLink.Core\Canonical\` once confirmed unused
- [ ] **A4.** Verify `GET /api/orders/{id}/audit` exists in `OrdersController`
- [ ] **A5.** Verify `GET /api/orders/{id}/status` exists in `OrdersController`

### Group B — Marketing pages (Next.js SSR — no react-helmet needed)
**Status:** ✅ Done. Do not repeat unless `STATUS.md` says a regression reopened it.

- [ ] **B1.** Build out `app/(marketing)/page.tsx` — full landing page:
  - Hero: "Stop reformatting purchase orders. Start delivering them."
  - Three-step explainer with visual flow
  - Testimonial placeholder
  - CTA → Clerk sign-up
  - `generateMetadata()` with proper OG tags
- [ ] **B2.** Build/update `app/(marketing)/pricing/page.tsx` — final billing ladder:
  Pilot (free 14 days), Growth (€149/mo), Operations (€399/mo), Integration (€999/mo), Enterprise (custom)
- [ ] **B3.** Build `app/(marketing)/how-it-works/page.tsx` — step-by-step + FAQ
- [ ] **B4.** Update `robots.txt` — allow `(marketing)` routes, disallow `(app)` routes

### Group C — Stripe billing
**Status:** ✅ Implemented. C2 final billing model reconciliation is also implemented.
Read `STATUS.md` and `docs/superpowers/specs/2026-05-24-stripe-billing-design.md`
before changing billing code.

Final plan ladder:

| Plan | Price | Orders | Suppliers |
|---|---:|---:|---:|
| Pilot | €0 / 14 days | 20 total during trial | 1 |
| Growth | €149/mo | 150/month | 5 |
| Operations | €399/mo | 500/month | 10 |
| Integration | €999/mo | 1,000/month | 20 |
| Enterprise | Custom, from €2,500/mo | Custom | Custom |

Pilot is not free forever and does not use Stripe. Expired Pilot accounts become
read-only: users can view previous data and billing, but cannot upload, transform,
deliver, or add suppliers. Paid self-serve Checkout supports only Growth,
Operations, and Integration. Enterprise is contact-sales/manual.

Live Stripe Checkout/Portal/webhook QA is still recommended before production billing launch.

### Group D — PO field mapping engine
**Status:** ✅ Done. Supplier-level CSV PO mapping config, mapping engine,
manipulators, test endpoint, and frontend `PoMappingEditor` are implemented.

### Group D2 — Buyer-side supplier delivery configuration
**Status:** ✅ Implemented for the HTTP-first path. Do not replan/rebuild it.

What exists now:
- Delivery state model distinguishes artifact generation from real delivery:
  `ready_to_deliver` → `delivering` → `delivered` or `delivery_failed`.
- Delivery credentials use authenticated `AesGcm` encryption.
- Supplier delivery config CRUD exists at GET/PUT/DELETE `/api/suppliers/{id}/delivery-config`.
- Safe test-fire exists at POST `/api/suppliers/{id}/delivery-config/test-fire`.
- `IDeliveryService` owns delivery workflow, delivery attempts, and dispatcher calls.
- `TransformOrderJob` enqueues delivery after successful transform.
- `HttpDeliveryDispatcher` is hardened with timeout support and safer failure messages.
- Frontend has `DeliveryConfigEditor` and a `Delivery` tab in `SupplierDockProfile`.
- SFTP/FTP remain intentionally deferred until HTTP delivery is production-proven.

Manual browser/Scalar test-fire against a live API session is still recommended before pushing delivery to real users.

### Group E — AI mapping suggestions (provider-neutral, OpenAI first)
**Status:** Not started. Use provider-neutral service design with OpenAI structured outputs as the first implementation.

Decision: **Do not hardwire Anthropic/Claude for Group E.** For line-level supplier SKU suggestions, the product needs low-cost, fast, strict JSON responses with confidence and provenance. Use OpenAI structured outputs first, keep the interface provider-neutral, and leave Claude/Anthropic as a future optional provider for heavier reasoning workflows.

- [ ] Add OpenAI SDK/package to the backend project that implements the provider.
- [ ] Add provider-neutral `IAiMappingService` contract.
- [ ] Add `OpenAiMappingService` first implementation using structured outputs / JSON schema.
- [ ] Config:
  - `Ai:Provider = "openai"`
  - `Ai:OpenAI:ApiKey`
  - `Ai:OpenAI:MappingModel = "gpt-5-mini"` by default, with `"gpt-5-nano"` acceptable for very cheap fallback testing.
- [ ] No-op when no AI API key is configured.
- [ ] Call the service after deterministic mapping lookup for unresolved lines.
- [ ] Do not auto-apply suggestions. Store/display them as suggestions only.
- [ ] Every suggestion must include:
  - suggested supplier item code
  - confidence
  - short reason
  - source/provenance, e.g. existing mappings, candidate catalog rows, buyer code/description evidence
- [ ] Frontend: pre-fill unresolved resolve inputs with AI suggestions, show `AI suggested` badge, confidence, and provenance. Avoid decorative sparkle copy.
- [ ] Future provider option: `ClaudeAiMappingService` may be added later behind the same `IAiMappingService`, but it is not the Group E default.

### Group F — PDF ingestion
- [ ] Add `PdfPig` to `ProcuLink.Transform`
- [ ] `PdfOrderParser : IPurchaseOrderParser` — text extraction + line parsing
- [ ] Accept `.pdf` in upload endpoint + FileUploadZone

### Group G — ERP connectors
- [ ] `IErpConnector` interface
- [ ] `ErplyConnector` (REST API) + `DirectoConnector` (XML API)
- [ ] New `destination_type` values: `erp_erply`, `erp_directo`

### Group H — Email polling (IMAP)
- [ ] `MailKit` in `ProcuLink.Worker`
- [ ] `EmailPollingJob` — recurring Hangfire job, every 5 min
- [ ] `email_config` jsonb on `organisations` + migration
- [ ] `PUT /api/settings/email` endpoint
- [ ] Email settings section in `app/(app)/settings/page.tsx`

---

## Coding conventions

### Next.js frontend
- **App Router only.** No Pages Router.
- **Server Components by default** — only add `'use client'` when component uses
  hooks, event handlers, or browser APIs.
- **TanStack Query in Client Components only** — wrap in a `QueryClientProvider`
  in a Client Component layout.
- **`@clerk/nextjs`** — use `auth()` in Server Components,
  `useAuth()`/`useUser()` in Client Components.
- **No `react-router-dom`** — use `next/link`, `next/navigation`.
- **SEO:** `generateMetadata()` in Server Components — no `react-helmet-async`.
- **Environment:** `NEXT_PUBLIC_*` prefix for client-side vars, nothing else.
- **bun** only — never npm.
- **TanStack Query** for all server state in Client Components.
- All API calls via `src/lib/api-client.ts`.

### .NET backend (unchanged)
- Controllers thin. Every service method takes `Guid organisationId`.
- All EF queries: `.Where(x => x.OrganisationId == organisationId)`.
- Hangfire jobs idempotent.
- No raw SQL.

---

## Environment variables

### `ProcuLink.Api\appsettings.Development.json`
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5435;Database=proculink_dev;Username=postgres;Password=postgres"
  },
  "Clerk": { "Authority": "https://golden-alpaca-43.clerk.accounts.dev" },
  "Storage": {
    "R2AccessKeyId": "",
    "R2SecretAccessKey": "",
    "R2BucketName": "proculink-dev",
    "R2Endpoint": "https://<accountid>.r2.cloudflarestorage.com"
  },
  "Stripe": {
    "SecretKey": "",
    "WebhookSecret": "",
    "GrowthPriceId": "",
    "OperationsPriceId": "",
    "IntegrationPriceId": ""
  },
  "Ai": {
    "Provider": "openai",
    "OpenAI": {
      "ApiKey": "",
      "MappingModel": "gpt-5-mini"
    }
  },
  "Sentry": { "Dsn": "" },
  "Frontend": { "Url": "" }
}
```

### `project-proculink\.env` (committed, Next.js)
```
NEXT_PUBLIC_API_BASE_URL=http://localhost:5223
NEXT_PUBLIC_USE_MOCK=false
```

### `project-proculink\.env.local` (gitignored)
```
NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY=pk_test_...
NEXT_PUBLIC_SENTRY_DSN=
```

---

## Key links
- Frontend: https://github.com/dimnovare/project-proculink
- Frontend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
- Backend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
- API dev: http://localhost:5223
- Scalar UI: http://localhost:5223/scalar
- Hangfire dashboard (dev): http://localhost:5223/hangfire
- Clerk: https://clerk.com (authority: golden-alpaca-43.clerk.accounts.dev)
- Neon.tech: https://neon.tech
- Cloudflare R2: https://dash.cloudflare.com
- Railway: https://railway.app
- Vercel: https://vercel.com
- Stripe: https://stripe.com

---

## Token/context discipline
- Start with `git status --short` and `git diff --stat`.
- Do not run full `git diff` unless asked.
- Do not read all markdown documentation automatically.
- Read only files directly relevant to the current task.

## What NOT to do
- ❌ `npm install` — **bun** only
- ❌ `npm install -g claude-mem` — use `/plugin` commands, never npm for claude-mem
- ❌ Lovable or Lovable-generated Vite/React code — ProcuLink UI is Claude Code + `/frontend-design` only
- ❌ Use `@clerk/clerk-react` — Next.js uses `@clerk/nextjs`
- ❌ Use Pages Router — App Router only
- ❌ `react-helmet-async` — use `generateMetadata()` in Server Components
- ❌ `react-router-dom` — use `next/link` and `next/navigation`
- ❌ `VITE_*` env vars after migration — use `NEXT_PUBLIC_*`
- ❌ EF queries without `org_id` scope — ever
- ❌ `useEffect` for data fetching — TanStack Query only
- ❌ Skip `/superpowers:brainstorm` for tasks touching ≥3 files
- ❌ Skip `/code-review` at end of a group
- ❌ Hardwire Group E to Anthropic/Claude — use provider-neutral AI mapping with OpenAI structured outputs first
- ❌ Raw SQL — EF Core only
- ❌ Hangfire jobs that are not idempotent
