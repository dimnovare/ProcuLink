# ProcuLink — Claude Code Project Memory

## What this project is

ProcuLink is a **B2B supplier-order bridge** for Baltic/Nordic distributors,
wholesalers, and manufacturers that receive orders in messy formats (CSV, XLSX)
and need them transformed into supplier-ready structured documents and delivered.

**Upload a buyer order file → validate → resolve mappings → transform → deliver.**

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

**What it does:** Auto-invokes when Claude detects frontend work. Enforces bold
typography, distinctive colour palettes, deliberate spacing, and creative layouts.
Prevents generic "AI slop" UI output.

**When to invoke explicitly:** Any new page, component, or significant UI change
in `project-proculink`. If Claude is about to generate a component, say
"use /frontend-design guidance for this" to make it explicit.

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
| New pages / major UI components | **`/frontend-design`** + Claude Code |
| UI component generation (framework-agnostic) | **Lovable** → copy to Next.js structure |
| End of each task group | **`/code-review`** before marking done |
| All .NET backend | **Claude Code** in `ProcuLink` solution |
| Next.js routing, Server Components, middleware | **Claude Code** only — not Lovable |

**Note on Lovable after Next.js migration:**
Lovable generates Vite React code. After migrating to Next.js, Lovable's GitHub
push-and-preview no longer "just works" for pages (the Vite preview won't match
Next.js patterns). However, the UI components Lovable generates (shadcn, Tailwind,
TypeScript React) are framework-agnostic and usable in Next.js after minor adaptation:
- Change `import { useNavigate } from 'react-router-dom'` → `import { useRouter } from 'next/navigation'`
- Add `'use client'` directive to interactive components
- Everything else (shadcn, Tailwind, TanStack Query) works identically

Recommended workflow: Use Lovable for component/UI polish. Use Claude Code + `/frontend-design`
for new pages, layouts, and anything requiring Next.js-specific patterns.

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
| ORM | EF Core 9 + Npgsql |
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
| **Next.js migration** | 🚧 **DO FIRST before Phase 4** |
| Phase 4 — Commercial | ⏳ After migration |

---

## Next.js migration plan

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
- [ ] **A1.** `bun remove lovable-tagger`
- [ ] **A2.** Remove `SupplierProfilesController.cs` (redundant — routes now in `SuppliersController`)
- [ ] **A3.** Remove `ProcuLink.Core\Canonical\` once confirmed unused
- [ ] **A4.** Verify `GET /api/orders/{id}/audit` exists in `OrdersController`
- [ ] **A5.** Verify `GET /api/orders/{id}/status` exists in `OrdersController`

### Group B — Marketing pages (Next.js SSR — no react-helmet needed)
- [ ] **B1.** Build out `app/(marketing)/page.tsx` — full landing page:
  - Hero: "Stop reformatting purchase orders. Start delivering them."
  - Three-step explainer with visual flow
  - Testimonial placeholder
  - CTA → Clerk sign-up
  - `generateMetadata()` with proper OG tags
- [ ] **B2.** Build `app/(marketing)/pricing/page.tsx` — three tiers:
  Starter (free, 20 orders/mo), Growth (€49/mo, 200 orders), Enterprise (custom)
- [ ] **B3.** Build `app/(marketing)/how-it-works/page.tsx` — step-by-step + FAQ
- [ ] **B4.** Update `robots.txt` — allow `(marketing)` routes, disallow `(app)` routes

### Group C — Stripe billing
- [ ] Add `Stripe.net` to `ProcuLink.Api.csproj`
- [ ] Add `stripe_customer_id`, `stripe_subscription_id` to `organisations` table + migration
- [ ] `POST /api/billing/checkout` — Stripe Checkout session for Growth plan
- [ ] `POST /api/billing/portal` — Stripe Customer Portal session
- [ ] `POST /api/billing/webhook` (no auth) — handle `checkout.session.completed` + `subscription.deleted`
- [ ] `GET /api/billing/status` — returns `{ plan, ordersThisMonth, orderLimit }`
- [ ] Enforce order limit in `OrdersController.Upload` → 429 with upgrade message
- [ ] `app/(app)/settings/page.tsx` — plan + usage display, upgrade + manage buttons

### Group D — PO field mapping engine
- [ ] `SupplierPoMapping` entity + JSONB `config_json` + EF migration
- [ ] `IPoMappingService` + `PoMappingService` — CRUD for mapping config
- [ ] `IFieldManipulator` interface + 8 manipulators (Replace, Trim, DateFormat, Concat, Fallback, Split, Multiply, Divide)
- [ ] `PoMappingEngine` in `ProcuLink.Transform` — applies template to raw CSV rows → `MappedOrder`
- [ ] `ParseOrderJob` updated — template-aware path (falls back to `CsvOrderParser` when no config)
- [ ] `GET/PUT /api/suppliers/{id}/po-mapping` + export/import endpoints
- [ ] `PoMappingEditor` frontend component on supplier detail page — visual field-by-field mapping UI
- [ ] Spec: `docs/superpowers/specs/2026-05-24-bulk-mapping-import-export-design.md`

### Group D2 — Supplier delivery configuration
- [ ] Per-supplier delivery config: protocol (HTTP/SFTP/FTP/webhook), auth (none/basic/OAuth2), URL, request body template, retry settings
- [ ] `SupplierDeliveryConfig` entity + JSONB config + EF migration
- [ ] `IDeliveryConnector` interface + HTTP connector implementation
- [ ] Test-fire capability: send a sample order to the configured endpoint, return response
- [ ] Frontend: delivery config tab on supplier detail page (visual, non-developer friendly)

### Group E — AI mapping suggestions (Claude API)
- [ ] Add `Anthropic.SDK` to `ProcuLink.Api.csproj`
- [ ] `IAiMappingService` + `ClaudeAiMappingService` — suggest supplier code from buyer code + description
- [ ] Call in `ParseOrderJob` for unresolved lines (no-op if `Anthropic:ApiKey` absent)
- [ ] Frontend: pre-fill resolve inputs with AI suggestions, show "✨ AI suggested" badge

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
  "Stripe": { "SecretKey": "", "WebhookSecret": "", "GrowthPriceId": "" },
  "Anthropic": { "ApiKey": "" },
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
- ❌ Use `@clerk/clerk-react` — Next.js uses `@clerk/nextjs`
- ❌ Use Pages Router — App Router only
- ❌ `react-helmet-async` — use `generateMetadata()` in Server Components
- ❌ `react-router-dom` — use `next/link` and `next/navigation`
- ❌ `VITE_*` env vars after migration — use `NEXT_PUBLIC_*`
- ❌ EF queries without `org_id` scope — ever
- ❌ `useEffect` for data fetching — TanStack Query only
- ❌ Skip `/superpowers:brainstorm` for tasks touching ≥3 files
- ❌ Skip `/code-review` at end of a group
- ❌ Call Anthropic API before Phase 4 Group E
- ❌ Raw SQL — EF Core only
- ❌ Hangfire jobs that are not idempotent

