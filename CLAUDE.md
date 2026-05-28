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

**Model routing policy:** read [`docs/CLAUDE_MODEL_ROUTING.md`](docs/CLAUDE_MODEL_ROUTING.md)
before starting any non-trivial task. It defines which capability tier
(cheap-fast / Sonnet / Opus) to use per task type, escalation/de-escalation
rules, subagent policy, and the `[FAST]` / `[STANDARD]` / `[CAREFUL]` /
`[OPUS-PLAN]` / `[OPUS-REVIEW]` prompt prefixes the user may apply.
**Default to Sonnet for implementation.** Use Opus only for architecture,
risky/cross-cutting reasoning, billing/security/tenancy review, or after
two failed Sonnet attempts on the same problem.

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
├── ProcuLink.Worker\                ← Hangfire jobs host (Phase 3+)
├── ProcuLink.Transform.Tests\       ← Transform unit tests
├── ProcuLink.Infrastructure.Tests\  ← Infrastructure + delivery unit tests
└── ProcuLink.Api.Tests\             ← Api/middleware/controller/job unit tests (added Phase 4.3)

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\  ← Frontend
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun
Framework: Next.js 15 (App Router)
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
| Phase 4 — Commercial | ✅ Groups C-H implemented — live QA and hardening next |
| Wave 3 — Invoice + ASN canonical models | ✅ Done — UBL 2.1 invoice parser, invoice/ASN entities, CSV/XML/JSON transforms, Hangfire job, controllers |
| Wave 4 — Zapier/Make.com integration layer | ✅ Done — API keys (`plk_`), org slug, integration subscriptions, `ApiKeyAuthHandler`, ingress/integration controllers, HMAC-SHA256 trigger firing, frontend tabs |

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
| **I** | UI/UX production polish + responsive QA | **In progress — pass 15 complete** |
| **J** | Live end-to-end QA + deployment hardening | In progress — code gaps fixed, live deployed QA remaining |
| **K** | Standards + engine hardening | ✅ Done — cXML 1.2 parser + transformer, standards matrix, canonical PO model (`2697115`) |
| **L** | Trust, onboarding + commercial readiness | In progress — Waves 1 + 2 merged to `main` (entity rename, /dpa /subprocessors /aup, /customers, /one-pager, cookie consent, PostHog frontend+backend SDK + taxonomy, sample-order endpoint, 4-step wizard, /welcome page, /help docs + in-app HelpSlideover, event emitters). Wave 3 pending: Phase 6.3 sample-button frontend, 7.2 Stripe `success_url`, 9.2 contact form, 10.3 /watch + book-a-demo, 10.4 cleanup. Waiting on founder config: PostHog keys, Clerk post-signup redirect, status page URL, Loom URL, Cal.com URL. See `STATUS.md` "Group L — deferred to Wave 3" + "waiting on founder configuration" sections. |

Group I remains the active implementation group unless the user explicitly
reprioritizes. The Bridge Layer is locked, but screens still need route-by-route
desktop/tablet/mobile QA, responsive layouts, empty/error states, and visible
defects fixed. Known example: the Wire Topology traveller/dot must never appear
detached from a visible wire.

---

## Latest committed implementation state (May 28 2026)

Read this before starting new work:

- **Group C2 billing reconciliation is implemented** in backend and frontend.
  - Backend commit: `18feb71 feat: reconcile final billing model backend`
  - Frontend commit: `6116af9 feat: reconcile final billing model frontend`
  - Status commit: `f957f16 docs: update billing reconciliation status`
- **Group D2 buyer-side supplier delivery config is implemented for the HTTP-first path.**
  - Key backend commits include `70f20bd`, `301b836`, `779a128`, `61832f7`, `09b36c3`, `ee20fbe`, `19c8e98`, `326529d`, `2d765fc`
  - Frontend commits: `7772f4a`, `748c6de`
- **Group E AI mapping suggestions are implemented** in backend and frontend.
  - Provider-neutral `IAiMappingService` with OpenAI structured outputs first.
  - Suggestions are stored on purchase order lines and exposed as line metadata.
  - Resolve UI pre-fills suggestions but visibly labels confidence, reason, and provenance.
- **Group F PDF ingestion is implemented** for text-based purchase-order PDFs.
  - `PdfOrderParser` uses `PdfPig` for text extraction plus conservative header/line parsing.
  - The API accepts `.pdf` uploads, and `FileUploadZone` accepts PDF files.
  - Scanned/image-only PDFs and OCR are deferred.
- **Group G ERP connectors are implemented** as delivery adapters.
  - `IErpConnector` plus Erply and Directo connectors exist.
  - `erp_erply` and `erp_directo` are accepted delivery protocol values.
  - The existing delivery workflow handles status, attempts, and test-fire rows.
  - These deliver generated artifacts; ERP-native order modeling remains future hardening.
- **Group H email polling is implemented** for Integration+ IMAP attachment ingestion.
  - `organisations.email_config` stores encrypted IMAP settings as JSONB.
  - `GET/PUT /api/settings/email` exposes org-scoped settings and billing-gated enablement.
  - `EmailPollingJob` runs from `ProcuLink.Worker` every 5 minutes through Hangfire.
  - CSV/XLSX/PDF attachments enter the existing create-stub and parse pipeline.
  - Body-only parsing and richer message-id dedupe are deferred.
- **Group I UI/UX polish is in progress, pass 15 complete.**
  - Passes 1-2: Wire Topology traveller/path/legend/port fixes, Playwright QA bypass added.
  - Pass 3: `/upload` mobile stack layout, `/settings` horizontal mobile tabs.
  - Pass 4: inbox mobile route cards + desktop table, removed `@tanstack/react-virtual`, dock/log/webhook mobile stacking.
  - Pass 5: supplier detail/mapping editor/PO mapping/delivery config mobile layouts.
  - Pass 6: billing/email API-unavailable states, bounded fetch timeouts, connector mobile cards, connector/webhook config panels.
  - Pass 7: mappings/rules/templates import-edit panels, rules mobile card list, template body edit.
  - Pass 8: upload selected-file/read-only/429 handling, plan usage in pipeline panel, supplier-limit vs billing-unavailable distinction.
  - Pass 9: connector/webhook/mapping/rule/template local QA feedback notices; notices moved into wrapped rows.
  - Pass 10: upload routes to returned order id (not hardcoded `/inbox/008412`); review Save/Copy/Download/delivered feedback; mobile review action bar layout.
  - Pass 11: `UploadWorkbench` loads supplier docks from `GET /api/suppliers`; non-mock uploads route to `/orders/{id}`; exported `isApiMockMode`.
  - Pass 12: Topology + Bridge visual calibration — `strokeFromWeight()`, staggered Bezier CPs, amber alert badges, `r=2.2` pulse, `WireTopologyLaneList` mobile, 28px `StatusJourney` nodes, `1fr/1.05fr/1.15fr` SpineReview grid, 2×2 mobile KPI grid.
  - Pass 13: `BridgeTopbar` auto-breadcrumb from pathname via `useAutoCrumb()`.
  - Pass 14: `BridgePageLoader` loading.tsx for 11 missing routes, `InboxView` mobile empty state, global `:focus-visible` ring, sidebar workspace-switcher accessible button, topbar aria-labels.
  - Pass 15: `SpineReview` wired to live `GET /api/orders/{id}` via `useQuery`; `buildNodesFromOrder()` maps Order → SpineNodeData[]; `BuyerName` added to `OrderDto`; loading skeleton + error/not-found gate added.
  - Continue live API/deployment QA for the full first-upload-to-delivery happy/error paths against a running backend before Group J. Group J should turn the current connector/webhook/mapping/rule/template local QA affordances into real persistence/test-fire verification.
- **Do not redo C2, D2, E, F, G, H, Wave 3, or Wave 4.** Treat them as implemented unless `STATUS.md` says a regression reopened them.
- **Manual/live QA still recommended:**
  - Stripe Checkout + Portal + webhook mapping with real Stripe test events.
  - HTTP delivery config test-fire against a running API session.
  - OpenAI-backed mapping suggestion with a real `Ai:OpenAI:ApiKey`.
  - Erply and Directo connector test-fire against ERP sandbox endpoints.
  - IMAP polling against a real mailbox/app password and supplier profile.
- **Group K — cXML 1.2 standards hardening is implemented.** `CxmlOrderParser`, `CxmlTransformService`, `OutputFormat.CXml`, standards matrix, canonical PO model docs. Merged to `main` (`2697115`).
- **Wave 1/2 code completeness verified (2026-05-28):** `EdifactOrderParser` + `UblOrderParser` have real parsing logic (Wave 1 complete). SFTP/S3 ingress, OCR (config-gated), and email-body extractor (API-only by design) are all wired (Wave 2 complete). `EdifactInvoiceParser`/`EdifactDesadvParser` stubs are Wave 3, not Wave 2.
- **Wave 3 — Invoice + ASN canonical models are implemented** (commit `3fbff22`):
  - `UblInvoiceParser` (full UBL 2.1), `EdifactInvoiceParser` + `EdifactDesadvParser` stubs (EdiFabric licence required; drop-in ready).
  - `InvoiceEntity` / `InvoiceLineEntity` / `AdvanceShippingNoticeEntity` / `AsnPackageEntity` / `AsnPackageLineEntity`.
  - `CsvInvoiceTransformService` / `XmlInvoiceTransformService` / `JsonInvoiceTransformService`.
  - `ParseInvoiceJob` (Hangfire, 3 retries). `InvoiceController` (upload/list/get/approve/download). `DesadvController` (202 Accepted + licence note).
  - 4 EF migrations: `AddInvoicesAndLines`, `AddAdvanceShippingNotices`, `AddTenantApiKeysAndOrgSlug`, `AddIntegrationSubscriptions`.
  - 12 new tests (7 UblInvoiceParser, 2 EdifactStub, 3 CsvInvoiceTransform).
- **Wave 4 — Zapier/Make.com integration layer is implemented** (commit `3fbff22`):
  - `ApiKeyHasher` in `Core.Security` (shared utility, no circular refs).
  - `TenantApiKey` (`plk_` prefix, HMAC-SHA256 hash, plaintext never stored) + `Organisation.Slug` (unique kebab-case, auto-generated in `TenantResolutionMiddleware`).
  - `IntegrationSubscription` (platform, eventType, targetUrl, AES-GCM encrypted HMAC secret).
  - `ApiKeyAuthHandler` — second ASP.NET Core auth scheme alongside Clerk JWT Bearer.
  - `IngressController` (`GET /api/ingress/{slug}/ping`, `POST /api/ingress/{slug}/orders`).
  - `IntegrationController` (CRUD + toggle). `ApiKeyController` (Clerk-auth CRUD).
  - `FireIntegrationTriggerJob` in `Infrastructure.Jobs` (HMAC-SHA256 `X-ProcuLink-Signature`, 3 retries, auto-deactivates after 3 failures).
  - Hooks: `OrderService` → `order.created`; `DeliveryService` → `order.delivered` / `order.failed`.
  - Frontend: Settings → **API Keys** tab (create/list/revoke, one-time raw key display) + **Connectors** tab (Zapier/Make CTAs + custom webhook CRUD).
  - 6 new tests (3 ApiKeyService, 3 ApiKeyHasher). `docs/integrations/SUBMISSION.md` (Zapier + Make.com submission checklist).
- **Fix (2026-05-28): JsonDocument EF InMemory value converter** — added `string` round-trip `ValueConverter<JsonDocument?, string?>` to `ProcuLinkDbContext` for all 5 jsonb columns. Updated all test-scoped `Ignore` lists for new Wave 3+4 entities. Resolved 48 pre-existing test failures (commit `367c07f`).
- **Fix (2026-05-28): Migration slug backfill** — `AddTenantApiKeysAndOrgSlug` migration now runs a SQL `UPDATE` to generate `kebab(name)-{first4uuid}` slugs for existing orgs before the unique index is created (commit `19078e2`).
- **Worker DI fix (2026-05-28):** `IIntegrationTriggerService` registered in `ProcuLink.Worker/Program.cs` (commit `4607d6d`).
- **Group L Wave 2 — sample-order onboarding chip is implemented** (commits `ffe7418` + `524b080`):
  - `ProcuLink.Api/Fixtures/sample-order.csv` — embedded 3-line EUR fixture (DEMO-2026-001, Northwind Trading OÜ).
  - `IsSample: bool` on `PurchaseOrderEntity` + `Supplier`; `Code: string?` on `Supplier`.
  - EF migration `20260528150709_AddIsSampleFlags` — adds `is_sample` + `code` columns to `purchase_orders` and `suppliers`.
  - `StripeBillingService.CountOrdersAsync` guards quota with `&& !o.IsSample` on both Pilot cumulative and paid-plan monthly branches.
  - `ISampleOrderService` (Core) + `SampleOrderService` (Infrastructure): idempotent `__sample__` supplier, fixture upload via `IFileStorageService`, `IsSample = true` order stub, parse-job enqueue via `IParseJobEnqueuer`, `sample_order_started` analytics event.
  - `POST /api/onboarding/sample-order` — `SampleOrderController` returns `{ orderId, isSample: true }`.
  - 3 new xUnit tests in `SampleOrderServiceTests`: supplier creation, supplier reuse, and quota exclusion (`!IsSample` filter).
  - **Do not redo.** Treat as implemented unless `STATUS.md` says a regression reopened it.
- **Group L Phase 4.3 — backend analytics event emitters are implemented** (commits `b7fa374`, `0220fd8`):
  - `IAnalyticsService` injected into 6 callsites: `TenantResolutionMiddleware`, `SuppliersController`, `ParseOrderJob`, `TransformOrderJob`, `DeliveryService`, `StripeBillingService`.
  - Events emitted: `org_created`, `first_supplier_added`, `first_upload_parsed`, `first_transform_succeeded`, `first_delivery_succeeded`, `billing_upgraded`, `billing_downgraded`, `billing_cancelled`.
  - All "first" events use `AnyAsync` org-scoped guard — idempotent across Hangfire retries.
  - Billing emit methods (`EmitBillingUpgradedAsync` / `EmitBillingDowngradedAsync` / `EmitBillingCancelledAsync`) added to `StripeBillingService` as concrete public methods (not on `IBillingService`); wiring to `BillingController` webhook handlers is a separate later chip.
  - New `ProcuLink.Api.Tests` project added to `ProcuLink.slnx`; 11 new tests (2 middleware + 2 suppliers + 2 parse job + 2 transform job + 3 billing).
  - `FakeAnalyticsService` in both `ProcuLink.Infrastructure.Tests/TestDoubles/` and `ProcuLink.Api.Tests/TestDoubles/`.
  - 2 new delivery emit tests in `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceEmitsFirstDeliverySucceededTests.cs`.
- **Last verified commands:**
  - `dotnet build ProcuLink.Api/ProcuLink.Api.csproj --no-restore` passed (API process locking DLLs; build Infrastructure + tests fine).
  - `dotnet test ProcuLink.slnx --no-restore` passed, **211 tests** (102 Transform + 11 Api.Tests + 98 Infrastructure), 0 failures.
  - `bun run build` in `project-proculink` passed; existing warnings remain for Sentry global error handler, Sentry `onRequestError`, Browserslist age, and Next ESLint plugin.

No remaining Phase 4 C-H group is open. Wave 3 and Wave 4 are complete. Group K is complete. Current implementation group is **Group L — trust, onboarding + commercial readiness** from the Phase 5 roadmap; Waves 1 + 2 are merged to `main`, Wave 3 is the remaining slice (sample button frontend, Stripe `success_url`, contact form, /watch + book-a-demo, dead-code cleanup). Several pieces are also waiting on founder configuration (PostHog project keys, Clerk post-signup redirect, status page URL, walkthrough Loom URL, Cal.com URL) — see `STATUS.md`. Group I (UI polish) is effectively complete through pass 15.

1. Read `STATUS.md`.
2. Read `docs/superpowers/plans/2026-05-26-production-hardening-roadmap.md`.
3. Read only task-relevant design docs, starting with `docs/design-system/00-agent-quick-brief.md` for Group I.
4. Use `/superpowers:brainstorm` and `/superpowers:write-plan` for any new task touching 3+ files.

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
