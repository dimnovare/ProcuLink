# ProcuLink — Codex Project Memory

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
├── AGENTS.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev :5223
├── ProcuLink.Core\                  ← Domain models + service interfaces
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2/Local storage
├── ProcuLink.Transform\             ← CSV/XLSX parsers + XML/CSV transform
└── ProcuLink.Worker\                ← Hangfire jobs host (Phase 3+)

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\  ← Frontend
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun
Framework: Next.js 15 (App Router)
```

---

## Codex plugins — install these, use them every session

These are real plugins with exact install commands sourced from their repos.
Install once globally. They persist across all sessions.

---

### 1. Superpowers — structured workflow framework
**Source:** https://github.com/obra/superpowers
**Install (inside a Codex session):**
```
/plugin marketplace add obra/superpowers-marketplace
/plugin install superpowers@superpowers-marketplace
```
Quit and restart Codex after install. Skills auto-inject on session start.

**What it does:** Forces Codex to plan before coding, write tests first, and
self-review before handing back. Prevents jumping straight to code on complex tasks.

**Slash commands:**
- `/superpowers:brainstorm` — explore requirements and design before any implementation.
  Codex asks questions instead of writing code immediately.
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
**Source:** https://github.com/anthropics/Codex/tree/main/plugins/frontend-design
**Install:**
```
/plugin install frontend-design@Codex-plugins-official
```
Or via npx: `npx Codex-plugins install @anthropics/Codex-plugins/frontend-design`

**What it does:** Provides production-grade UI judgement, layout critique, and
polish. For ProcuLink it must execute the locked local design system, not invent
a new visual direction.

**When to invoke explicitly:** Any new page, component, or significant UI change
in `project-proculink`. If Codex is about to generate a component, say
"use /frontend-design guidance for this" to make it explicit.

**Design source of truth:** `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\docs\design-system`.
Read `00-agent-quick-brief.md` first, then only the specific design files needed
for the current task. The locked direction is Direction 4 - The Bridge Layer,
supported by Direction 3 - System Identity.

---

### 3. code-review — automated PR review
**Source:** https://github.com/anthropics/Codex/blob/main/plugins/code-review/README.md
**Install:**
```
/plugin install code-review@Codex-plugins-official
```
Or via npx: `npx Codex-plugins install @anthropics/Codex-plugins/code-review`

**What it does:** Runs 5 parallel Sonnet agents checking: AGENTS.md compliance,
bug detection, historical context, PR history, and code comments. Confidence-based
scoring filters false positives.

**Command:** `/code-review`

**When to invoke:** Run `/code-review` at the end of every task group (A, B, C…)
before marking it complete. Never skip. Paste the diff or describe what changed.

---

### 4. Codex-mem — persistent session memory
**Source:** https://github.com/thedotmack/Codex-mem
**Install:**
```
/plugin marketplace add thedotmack/Codex-mem
/plugin install Codex-mem
```
Then restart Codex.

**⚠️ Critical:** Do NOT run `npm install -g Codex-mem` — that installs only the
SDK library without the hooks or worker service. Always use `/plugin` commands above.
Requires Node.js 18+.

**What it does:** Captures everything Codex does during sessions (tool calls,
decisions, file changes), compresses with AI, and injects relevant context into
future sessions via SQLite + vector search. Sessions no longer start cold.

**How it works:** Fully automatic after install — no manual commands needed.
Context injects at session start. To search past sessions explicitly, ask:
"What did we do with the order parsing last time?" or
"What bugs did we fix in the transform pipeline?"

**Optional manual:** After a major session, ask Codex to summarise key decisions
so Codex-mem captures them with higher importance.

---

## Tool division of labour

| Task | Tool |
|---|---|
| Complex feature planning (≥3 files) | **`/superpowers:brainstorm`** first |
| Implementation from a plan | **`/superpowers:execute-plan`** |
| New pages / major UI components | Local design system + **`/frontend-design`** + Codex |
| UI component generation | Codex/Claude only, using `docs/design-system` |
| End of each task group | **`/code-review`** before marking done |
| All .NET backend | **Codex** in `ProcuLink` solution |
| Next.js routing, Server Components, middleware | **Codex** only |

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
| Phase 4 — Commercial | ✅ Groups C-H implemented — live QA and hardening next |

---

## Current direction — production hardening

Deep-research review on May 26 2026 confirmed the same direction as `STATUS.md`:
ProcuLink should now be treated as a real working product, not a prototype or
simple MVP. Do not add broad new engines on top of visibly rough UX.

Next work is grouped as **Phase 5 — Production Hardening And Standards**.
Read `docs/superpowers/plans/2026-05-26-production-hardening-roadmap.md`
before writing implementation plans.

| Group | Workstream | Status |
|---|---|---|
| **I** | UI/UX production polish + responsive QA | **In progress — pass 8 complete** |
| **J** | Live end-to-end QA + deployment hardening | Planned after I |
| **K** | Standards + engine hardening | Planned after I/J scoping |
| **L** | Trust, onboarding + commercial readiness | Planned; can overlap after I starts |

Group I must continue unless the user explicitly reprioritizes. Passes 1-8 are
complete: topology/wire defects, mobile shell, upload/settings, inbox/docks/logs,
supplier detail/mapping/delivery, settings/connector/webhook states,
mappings/rules/templates panels, and upload/supplier plan-gated states have all
been screenshot-tested and patched. The Bridge Layer is locked, but remaining
live-flow QA still needs connector/webhook/mapping/rule/template save/test-fire
behavior and first-upload-to-delivery happy/error paths against a running API.
Wire Topology rules are explicit: same-lane wires may be straight, cross-lane
wires arc, every wire uses the same visible gradient stroke, shared ports fan out,
alert counters stay tethered to their route, and the legend must not overlap
buyer/supplier pills.

---

## Frontend state — current, not historical

The frontend migration is complete. Treat `project-proculink` as a native **Next.js 15 App Router** application.

Current frontend rules:
- Use `src/app/` App Router routes only.
- Use `@clerk/nextjs`, `middleware.ts`, and Clerk server/client helpers for auth.
- Use `NEXT_PUBLIC_*` variables for client-visible values.
- Use `next/link` and `next/navigation`; never use `react-router-dom`.
- Use `bun` only.
- Do not use Vite, Vite env vars, Vite previews, `index.html`, `src/main.tsx`, or `@vitejs/*` packages.
- Do not use Lovable-generated components or routing patterns.
- Playwright is installed in the frontend for visual QA. Screenshots belong in
  `.qa-screenshots/` and must stay uncommitted.
- For local protected-route screenshots only, start the frontend with
  `PROCULINK_QA_BYPASS_AUTH=true bun run dev -- --hostname 127.0.0.1 --port 8082`.
  The bypass is disabled in production by `NODE_ENV`.

If older reports or commits mention Vite, React Router, Starter pricing, or Lovable, treat that as archived pre-migration history, not current guidance.

---
## Implemented commercial groups C-H

This section is implementation history and capability reference. Do not execute
unchecked historical checklist items unless `STATUS.md` explicitly reopens them.

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
**Status:** ✅ Implemented, but final billing model reconciliation is required.
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

### Group D — PO field mapping engine
**Status:** ✅ Done. Supplier-level CSV PO mapping config, mapping engine,
manipulators, test endpoint, and frontend `PoMappingEditor` are implemented.

### Group D2 — Buyer-side supplier delivery configuration
**Status:** ✅ Implemented for the HTTP-first path. Live HTTP test-fire QA is still
recommended before production use. SFTP/FTP remain future hardening.

- Delivery state model must distinguish artifact generation from actual supplier delivery:
  `transformed` / `ready_to_deliver` → `delivering` → `delivered` or `delivery_failed`.
- Delivery service, audit rows, idempotency, retry policy, safe test-fire, authenticated
  credential storage, and HTTP/webhook dispatch are implemented.
- Keep future delivery UI non-developer friendly: supplier dock → Delivery tab → protocol,
  endpoint/path, credentials, file naming, auto/manual toggle, test-fire result, and recent attempts.

### Group E — AI mapping suggestions (provider-neutral, OpenAI first)
**Status:** Implemented. Live OpenAI provider QA still recommended before production use.

Decision: **Do not hardwire Anthropic/Claude for Group E.** For line-level supplier SKU suggestions, the product needs low-cost, fast, strict JSON responses with confidence and provenance. Use OpenAI structured outputs first, keep the interface provider-neutral, and leave Claude/Anthropic as a future optional provider for heavier reasoning workflows.

- [x] Add OpenAI SDK/package to the backend project that implements the provider.
- [x] Add provider-neutral `IAiMappingService` contract.
- [x] Add `OpenAiMappingService` first implementation using structured outputs / JSON schema.
- [x] Config:
  - `Ai:Provider = "openai"`
  - `Ai:OpenAI:ApiKey`
  - `Ai:OpenAI:MappingModel = "gpt-5-mini"` by default, with `"gpt-5-nano"` acceptable for very cheap fallback testing.
- [x] No-op when no AI API key is configured.
- [x] Call the service after deterministic mapping lookup for unresolved lines.
- [x] Do not auto-apply suggestions. Store/display them as suggestions only.
- [x] Every suggestion must include:
  - suggested supplier item code
  - confidence
  - short reason
  - source/provenance, e.g. existing mappings, candidate catalog rows, buyer code/description evidence
- [x] Frontend: pre-fill unresolved resolve inputs with AI suggestions, show `AI suggested` badge, confidence, and provenance. Avoid decorative sparkle copy.
- [ ] Future provider option: `ClaudeAiMappingService` may be added later behind the same `IAiMappingService`, but it is not the Group E default.

### Group F — PDF ingestion
**Status:** ✅ Implemented for text-based purchase-order PDFs. Scanned/image-only PDFs and OCR are deferred.

- [x] Add `PdfPig` to `ProcuLink.Transform`
- [x] `PdfOrderParser : IPurchaseOrderParser` — text extraction + line parsing
- [x] Accept `.pdf` in upload endpoint + FileUploadZone

### Group G — ERP connectors
**Status:** ✅ Implemented as delivery adapters for already-generated artifacts. ERP-native order modeling and supplier-specific ERP payload transforms remain future hardening.

- [x] `IErpConnector` interface
- [x] `ErplyConnector` (REST API) + `DirectoConnector` (XML API)
- [x] New `destination_type`/protocol values: `erp_erply`, `erp_directo`

### Group H — Email polling (IMAP)
**Status:** ✅ Implemented for Integration+ IMAP attachment ingestion. Live IMAP mailbox QA is still recommended before production use.

- [x] `MailKit` in `ProcuLink.Worker`
- [x] `EmailPollingJob` — recurring Hangfire job, every 5 min
- [x] `email_config` jsonb on `organisations` + migration
- [x] `PUT /api/settings/email` endpoint
- [x] Email settings section in `app/(app)/settings/page.tsx`

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

## What NOT to do
- ❌ `npm install` — **bun** only
- ❌ `npm install -g Codex-mem` — use `/plugin` commands, never npm for Codex-mem
- ❌ Lovable or Lovable-generated Vite/React code — ProcuLink UI is Claude/Codex + `/frontend-design` only
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
