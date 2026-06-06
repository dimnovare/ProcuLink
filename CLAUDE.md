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

## Current direction — WIN THE FIRST CUSTOMERS (freeze features)

> **⚠️ ROADMAP UPDATED 2026-05-30** after the investor-grade + four-lens analyses.
> Strategic source of truth: [investor-analysis](docs/strategy/2026-05-30-investor-analysis.md)
> · [four-lens](docs/strategy/2026-05-30-four-lens-product-analysis.md)
> · [pricing-proposal](docs/strategy/2026-05-30-pricing-proposal.md).
>
> **The bottleneck is SELLING, not features.** The core engine (parse → map → validate →
> transform → deliver, audit trail, AES-GCM credentials) is real and tested — but it was
> overbuilt ~5×, never shown to a customer, and never run live end-to-end. ProcuLink is a
> **€1–3M ARR bootstrap** (profitable at €300–500k in 18–24 months), **not** a venture-scale
> "international standard." Those identities require opposite behaviours; the near-term plan
> commits to the bootstrap.
>
> **Wedge:** Baltic IT distributors/wholesalers on **Erply/Directo**, Estonia-first. Sell to
> the **buyer** (procurement coordinator — approves €399 without a committee). ICP: 50–400
> employees, 100–500 POs/mo to 3–20 suppliers, today handled in Excel.
>
> **Near-term plan — the ONLY things before the first paid pilot:**
> 1. **SSRF allowlist (P0 security) ✅ DONE** — `OutboundRequestGuard` (loopback/RFC-1918/link-local
>    169.254/cloud-metadata/IPv6 ULA/IPv4-mapped) wired into all 4 dispatchers + full tests. JWT
>    `ValidateAudience=false` is correct Clerk design (compensated by `azp` validation in
>    `ClerkTokenValidation`). Cross-tenant `FindAsync` P0 ✅ `54e1cf7`; all-zero AES-key P0 ✅
>    `ee7752c` (rotate the dev key out of git once billing lands). *Do NOT re-implement these.*
> 2. **Erply/Directo starter mapping templates ✅ DONE** — embedded fixtures + `StarterTemplateService`
>    + `GET /api/po-mapping-templates` + `POST /api/suppliers/{id}/po-mapping/apply-template`, and a
>    one-click "Apply starter template" control in the PO mapping editor. *Human must still verify the
>    assumed Erply/Directo column names against a real export (see
>    `docs/superpowers/specs/2026-06-03-erply-directo-mapping-templates.md`).*
> 3. **Exception dashboard ✅ DONE** — `/operations/exceptions` shipped; plus a new operator
>    `/operations/health` view (`/api/ops/health` + dead-letter + requeue).
> 4. **Live end-to-end QA ✅ PROVEN incl. SUCCESSFUL delivery** — a real order reached `delivered`
>    (code 200) live against a controlled endpoint (2026-06-03 night), browser + API verified, single
>    worker. Honest `delivery_failed` path also proven. *Only a delivery against a real SUPPLIER's
>    endpoint remains untested (needs a real supplier).*
> 5. **Pricing ✅ shipped:** Operations €399 anchor · **Distributor €1,499** (2,500 orders / 30 suppliers) ·
>    Enterprise from €2,500. **The per-supplier onboarding fee (€500×3 then €150) is RETIRED (founder, 2026-06-06)** —
>    higher tiers now include hands-on founder-led onboarding with no separate fee; user-facing copy reconciled
>    (`plans.ts` `SETUP_FEE_NOTE`, `pricing/page.tsx`, `ROICalculator.tsx`). *Still TODO: create the Stripe
>    Distributor product + `DistributorPriceId`; revise the dated 2026-05-30 strategy memos + stripe-go-live-checklist.*
> 6. **Extend the pilot 14 → 60 days**, then **put one real Markit PO in front of one real supplier
>    before writing another line of feature code.**
>
> **Current execution focus (2026-06-02):** make the primary PO path boringly reliable:
> upload -> parse -> review exceptions -> transform -> deliver -> audit. Follow
> `docs/superpowers/plans/2026-06-01-boringly-reliable-po-loop.md`. Do not broaden
> invoices/ASN/PEPPOL or add new channels until the first PO path has repeatable
> live happy/error QA. Tasks 1-5 are implemented: XML parser routing, returned
> line state, manual-review E2E, intake docs, and SFTP/S3 default-supplier
> safety. Task 6 is closed locally. The primary browser path is verified:
> `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/live-po-loop.spec.ts`
> drives CSV upload -> `/upload/preview/<orderId>` -> manual supplier-code entry
> -> save mapping -> `/inbox/<orderId>` -> send/transform/deliver -> missing
> delivery-config failure panel -> retry feedback. Direct API + Worker smoke also
> verifies auditable `delivery_attempts`; transform returns 409 while parsing, and
> delivery failures surface the latest attempt error on `GET /api/orders/{id}`.
> Failure-state browser QA is verified by
> `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/live-po-failure-states.spec.ts`:
> no supplier available, unsupported file format, scanned/textless PDF with OCR
> disabled, and supplier HTTP 4xx rejection. Next gate: repeat the same
> happy/error path against Railway/Vercel before broadening engines or channels.
>
> **FREEZE until there are paying customers** (real engineering, but none of it wins customer #1):
> Zapier/Make layer, invoice/ASN/DESADV, extra EDI formats, cross-org mapping library, RBAC/SCIM,
> PunchOut, OCR productization beyond the existing config-gated fallback, the standards-comparison screen, i18n, and the old "international standard" breadth
> (Horizons M/N/O/P/Q/R/S below). The one exception worth finishing: the **Bridge Layer frontend
> redesign** (`feat/bridge-layer-redesign`) — for demo credibility.
>
> **Strategic fork (founder's call):** "Baltic bootstrap" vs "international standard." The analysis
> recommends bootstrap. **Everything below this box is the earlier (2026-05-28) aspirational plan —
> now ON HOLD**, kept for reference, not the active roadmap.

ProcuLink's product thesis as of 2026-05-28: become the **international standard
for outbound B2B purchase order routing**. Any input format / channel →
canonical PO → any output format / channel. Best-in-class for 30-year
procurement veterans (depth, density, standards visibility). Effortless for
first-time users (wizard, templates, magic mapping preview, AI defaults).
Standards-fit for every supplier shape (Cinderella's-shoe-into-any-format).
Cost-effective versus SPS Commerce / TrueCommerce / Babelway / Pagero.

The next 4–6 weeks are tracked as **Phase 6 — International Standard**.
Source of truth for the forward plan:
`docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md`.
Positioning rationale: `docs/strategy/international-standard-thesis.md`.

The Learn loop (`Parse → Normalize → Validate → Review → Transform →
Deliver → Learn`) remains the long-term moat. Standards depth + channel
breadth + a single great UX are the next 6 months of execution.

### 3-Horizon roadmap

| Horizon | Theme | Timeline | Groups |
|---|---|---|---|
| **1** | Production Ready + Effortless | next 4–6 weeks | J (live QA), J2 (purge mock/demo residue), L expanded (onboarding wizard + magic mapping preview + in-app help + per-industry templates + analytics) |
| **2** | Standards Backbone + Channel Breadth | Q4 2026 | M (UBL / Peppol BIS / EDIFACT / X12 / JSON / ISO 20022 reference), N (SFTP / FTPS / SMTP / AS2 / AS4 / PEPPOL AP / webhook-in — partner-wrap first for AS2 + PEPPOL), O (retry queue / rejection capture / ACK round-trip / SLA timers) |
| **3** | Network Effects | Q1 2027+ | P (RBAC + SCIM), Q (supplier mapping library), R (i18n EN/DE/FR/ES/IT/PL), S (UBL Invoice + DESADV round-trip + 3-way match prep) |

Phase 5 status carried into Phase 6: Group I (UI polish) is effectively
complete through pass 15. Group K (cXML 1.2 + standards matrix + canonical
PO model) shipped. Group L Waves 1+2+3 shipped. Horizon 1 picks up with
Group J live QA, the new Group J2 demo-data purge, and an expanded Group L
building the onboarding / mapping / help experience.

**Direction note (2026-05-29):** The earlier "dual-persona UX" toggle
(default vs expert mode) was dropped before any downstream component
adopted it. Reason: successful B2B SaaS products (Linear, Stripe, Notion,
Vercel, Railway) all ship ONE great experience with smart defaults +
progressive disclosure + a Command Palette for power features — not
explicit user-mode toggles. Standards visibility and power-user
affordances will be surfaced via the existing Command Palette (Cmd+K),
per-screen column selectors, and contextual disclosure, not a global
mode flag. The Bridge Layer direction is still locked.

Backend test count: **213** (102 Transform + 11 Api.Tests + 100 Infrastructure).
Waiting on founder configuration only for Group L Wave 3 to function in
production: PostHog keys, Clerk post-signup redirect,
`NEXT_PUBLIC_STATUS_URL`, `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`,
`NEXT_PUBLIC_BOOK_DEMO_URL`, optional SMTP for the support form.

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
- **Group F PDF ingestion — Phase 1 (text→LLM) + Phase 2 (vision for scanned PDFs) SHIPPED 2026-06-05** (`feat/pdf-llm-extraction` merging to `main`; 780 backend tests green; live-verified). Spec: `docs/superpowers/plans/2026-06-05-pdf-llm-extraction.md`; benchmark-proven on 22 real Markit docs.
  - **Shipped (Phase 1):** the PRIMARY PDF path is **text→LLM structured extraction** → canonical `ParsedOrder`. PdfPig extracts the digital text layer; an OpenAI extractor structures it (strict JSON schema mirroring the canonical model — 5 header + 6 line fields; the LLM never emits a supplier item code, resolved downstream). Anti-hallucination validation: every emitted number must appear verbatim in the source text, and qty×unit-price must reconcile with the stated line amount; suspect lines are flagged "needs review" so they surface in `/operations/exceptions` instead of delivering blind. The fixed-column regex `PdfOrderParser` is now the deterministic FALLBACK only (no OpenAI key / offline / extraction fails or low-confidence). New config key `Ai:OpenAI:ExtractionModel` (falls back to `Ai:OpenAI:MappingModel`, then `gpt-5-mini`); extractor is a safe no-op when no key is set.
  - **Azure Document Intelligence REMOVED ENTIRELY:** the `Azure.AI.DocumentIntelligence` package, `AzureDocumentIntelligenceOcrService`, and all `Ocr:Azure:*` / `Ocr__Azure__*` config keys are gone. The `IDocumentOcrService` seam is KEPT; it now resolves to `RapidOcrDocumentOcrService` (self-hosted, no-egress) when `NoEgressOcr:Enabled=true`, and otherwise to `NoOpOcrService` (the safe default deploy).
  - **Phase 2 (vision for scanned PDFs) SHIPPED:** scanned / image-only PDFs (no text layer) are now supported via an AI **vision fallback** — when PdfPig finds no text, the system rasterizes the leading pages (**PDFtoImage + SkiaSharp**, both permissive; self-contained native assets, **no Dockerfile / system-package change** — verified loading on the Debian `aspnet:8.0` base) and extracts via the vision-capable OpenAI model using the same strict schema. Live-verified end to end (image-only PDF → vision → structured order on `gpt-4o-mini`). Honest caveat: vision has no text layer to verify numbers against, so **every line from a scanned PDF is flagged for human review** (surfaces in `/operations/exceptions` / order review) — assisted, not silent/auto-delivered. A scanned PDF the model still can't read (illegible) still fails with "This PDF looks scanned or image-only — we couldn't extract any text." Text-based PDFs remain the high-confidence primary path. Privacy: the vision path sends rasterized page **images** to OpenAI (same EU-residency / DPA / zero-retention considerations as the text path).
  - **Phase 3 — self-hosted no-egress OCR SHIPPED (opt-in, merging to `main`):** "no data leaves your environment" OCR is now AVAILABLE as an opt-in capability. `RapidOcrDocumentOcrService` (backed by **RapidOcrNet 2.0.0** — PP-OCRv5 via ONNX Runtime, **Apache-2.0 code AND weights**, ~12 MB bundled models, in-process, no GPU, NO external network calls) implements the existing `IDocumentOcrService` seam, replacing the no-op. Enablement requires TWO opt-ins: (1) GLOBAL `NoEgressOcr:Enabled=true` (Railway env form `NoEgressOcr__Enabled=true`) on BOTH the API and the Worker — registers the real engine instead of `NoOpOcrService`; without it no models load and the default deploy is byte-for-byte unchanged (ships dormant + safe); and (2) PER-ORG `Organisation.SelfHostedOcr=true` (DB column `self_hosted_ocr`, additive migration `AddSelfHostedOcrFlag`) — marks that org no-egress. For a no-egress org the WHOLE document ingest/parse pipeline is no-egress — nothing sends that org's data to OpenAI: PDF parsing is routed to the DETERMINISTIC parser with scanned/image-only pages OCR'd by the self-hosted RapidOcrNet engine (no OpenAI vision); AI mapping is gated — both line-level SKU suggestions and the magic auto-map field suggester (`OpenAiMappingService`), so unresolved lines/fields go to human review; email-body NLP extraction is gated (skipped); and the one-click AI schema-inference setup tool (`SchemaInferenceController` / `OpenAiSchemaInferencer`) is also gated (returns empty — the org uses the manual mapping editor). There is no remaining OpenAI touchpoint in the ingest/parse pipeline for a no-egress org. Native deps: both Dockerfiles add `libgomp1` + `libfontconfig1` to the runtime stage (verified on the `aspnet:8.0` base). No-egress is an ENTERPRISE / config capability enabled per-org by an operator, NOT a self-serve UI toggle. Honest caveat: even with self-hosted OCR, scanned/image-only lines are still review-flagged (no text layer to verify numbers against) and illegible scans still fail with the "scanned or image-only" message — assisted, not silent. Supplier/totals/tax/per-line-delivery-date enrichment + a PO-vs-invoice classifier shipped as **Phase 4**.
  - Privacy: real customer PO text → OpenAI needs an EU-residency project + DPA + zero-retention; the extractor is a no-op without a key (safe default). OpenAI is now the document-extraction processor; Azure Document Intelligence is no longer a subprocessor. User-facing capability copy is reconciled to reality (offer⇔works).
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
- **Boringly reliable PO loop update (2026-06-01):**
  - Development-only backend QA auth scheme added: set `PROCULINK_QA_BYPASS_AUTH=true` while `ASPNETCORE_ENVIRONMENT=Development`.
  - Local live API QA also needs a valid `Delivery__EncryptionKey` (32-byte base64), because supplier/delivery endpoints resolve encrypted delivery config services.
  - `CsvOrderParser` now removes header punctuation and supports common procurement aliases (`po_number`, `PO Number`, `po`, `line_no`, `qty`, `unit_price`, `sku`, `buyer_code`).
  - Verified live upload smoke: `PLAYWRIGHT_API_URL=http://localhost:5223 bun run test:e2e:live -- tests/e2e/magic-mapping-preview.spec.ts -g "upload a file and land"` passed.
  - Still open: live QA from preview through unresolved-line review, save mapping, transform, delivery-config-missing error, manual delivery, and delivery audit/rejection states.
- **Do not redo C2, D2, E, F, G, H, Wave 3, or Wave 4.** Treat them as implemented unless `STATUS.md` says a regression reopened them.
- **Manual/live QA still recommended:**
  - Stripe Checkout + Portal + webhook mapping with real Stripe test events.
  - HTTP delivery config test-fire against a running API session.
  - OpenAI-backed mapping suggestion with a real `Ai:OpenAI:ApiKey`.
  - Erply and Directo connector test-fire against ERP sandbox endpoints.
  - IMAP polling against a real mailbox/app password and supplier profile.
- **Group K — cXML 1.2 standards hardening is implemented.** `CxmlOrderParser`, `CxmlTransformService`, `OutputFormat.CXml`, standards matrix, canonical PO model docs. Merged to `main` (`2697115`).
- **Wave 1/2 code completeness verified (2026-05-28):** `EdifactOrderParser` + `UblOrderParser` have real parsing logic (Wave 1 complete). SFTP/S3 ingress, OCR (config-gated), and email-body extractor (API-only by design) are all wired (Wave 2 complete). `EdifactInvoiceParser`/`EdifactDesadvParser` stubs are Wave 3, not Wave 2.
- **Inbound/API docs:** `docs/integrations/ORDER_APIS.md` is the current reference for browser upload, IMAP, hosted inbound email webhook, inbound REST API, SFTP/S3 polling status, outbound webhook signing, and OCR setup. Hosted inbound email and inbound REST API have backend support; SFTP/S3 polling is hardened with `default_supplier_id` and no longer imports with `Guid.Empty`, but remains assisted/internal until setup/test-fire UX exists.
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

No remaining Phase 4 C-H group is open. Wave 3 and Wave 4 (Invoice/ASN + Zapier/Make.com) are complete. Group K is complete. **Group L is fully shipped on `main` both repos** — Waves 1 + 2 + 3 all merged, all feature branches deleted local + remote, all chip stashes cleared. The only remaining work is founder configuration (PostHog keys, Clerk post-signup redirect, `NEXT_PUBLIC_STATUS_URL`, `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL`, optional SMTP) — see `STATUS.md` "Group L — waiting on founder configuration / external setup" table. Group I (UI polish) is effectively complete through pass 15.

1. Read `STATUS.md`.
2. Read the current strategy memos in `docs/strategy/` (investor-analysis + four-lens) for priorities.
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
Read `STATUS.md` and `docs/strategy/2026-05-30-pricing-proposal.md`
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
**Status:** ✅ Implemented for both text-based AND scanned purchase-order PDFs. **Phase 1 text→LLM extraction is the PRIMARY path** (`feat/pdf-llm-extraction` merging to `main`, 780 backend tests green, live-verified); the regex `PdfOrderParser` is the deterministic fallback. Azure Document Intelligence removed entirely. **Phase 2 (vision fallback for scanned/image-only PDFs) SHIPPED** — PDFtoImage + SkiaSharp rasterize no-text PDFs (self-contained native assets, **no Dockerfile change**), extracted via the vision-capable OpenAI model; every scanned-PDF line is flagged for human review (assisted, not auto-delivered); illegible scans still fail with the "scanned or image-only" message. **Phase 3 self-hosted no-egress OCR SHIPPED** as an opt-in — `RapidOcrDocumentOcrService` (RapidOcrNet 2.0.0, PP-OCRv5/ONNX, Apache-2.0 code+weights, in-process, no external network calls) behind GLOBAL `NoEgressOcr:Enabled` (API + Worker) + PER-ORG `Organisation.SelfHostedOcr`; for such orgs the WHOLE ingest/parse pipeline is no-egress (deterministic PDF parse + self-hosted OCR; AI mapping — line-SKU + the magic auto-map field suggester — email-body NLP, and the AI schema-inference tool all gated → human review / manual editor), default prod deploy ships dormant + safe; scanned lines still review-flagged. Supplier/totals/tax enrichment + PO-vs-invoice classifier shipped as Phase 4.

- [x] Add `PdfPig` to `ProcuLink.Transform`
- [x] `PdfOrderParser : IPurchaseOrderParser` — text extraction + line parsing (now the deterministic fallback)
- [x] Text→LLM structured extraction (PdfPig text → OpenAI strict JSON schema → canonical `ParsedOrder`), with verbatim-number + qty×price anti-hallucination validation flagging suspect lines for review
- [x] Phase 2 vision fallback for scanned/image-only PDFs (PDFtoImage + SkiaSharp rasterize → vision OpenAI model; no Dockerfile change; all scanned-PDF lines review-flagged)
- [x] Phase 3 self-hosted no-egress OCR (`RapidOcrDocumentOcrService` / RapidOcrNet 2.0.0, Apache-2.0 code+weights, in-process, no external calls) — opt-in via GLOBAL `NoEgressOcr:Enabled` + PER-ORG `Organisation.SelfHostedOcr` (`AddSelfHostedOcrFlag` migration); whole ingest/parse pipeline no-egress for such orgs (AI vision/SKU mapping/field auto-map/email-body NLP/schema-inference all gated); `libgomp1` + `libfontconfig1` added to both Dockerfile runtime stages; default deploy dormant + safe
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

### Product-level rules (Phase 6+)

These rules apply to **every new screen and every new field** from
Phase 6 (2026-05-28) onward. They are durable product invariants, not
group-scoped tasks.

**One great experience rule.** ProcuLink ships ONE great UX, not a
"default / expert" toggle. Successful B2B SaaS products (Linear, Stripe,
Notion, Vercel, Railway) all use this approach: smart defaults +
progressive disclosure + a Command Palette for power features. Users
don't self-identify as novice or expert, and mode toggles add cognitive
load without measurable benefit.

- Wizards, per-industry templates, AI-pre-filled fields with visible
  confidence + provenance, and sensible defaults are for everyone.
- Density and power affordances (raw JSON/XML/EDI envelopes, hotkeys,
  inline-edit-of-anything, standards mappings) are surfaced via the
  existing Command Palette (Cmd+K), per-table column selectors, and
  contextual disclosure — discoverable by anyone who needs them.
- Don't add `localStorage`-backed user-mode flags. The earlier
  `useViewMode()` hook + `<ViewModeToggle />` were removed before
  adoption (2026-05-29).

**Standards-visibility rule.** Any field in a transform / mapping
context must be able to surface its standards mapping (UBL / EDIFACT /
X12 / cXML / Peppol BIS / ISO 20022) on demand. The user must be able
to see "this field maps to UBL `cbc:ID` / EDIFACT `BGM 1004` / X12
`BEG03` / cXML `OrderRequestHeader@orderID`" without leaving the screen.

- The canonical PO model is the join key. Each field on `ParsedOrder` /
  `ParsedOrderLine` carries its standards references in
  `docs/standards-matrix.md` § "Canonical PO Model fields".
- Surface standards via: an info icon next to the field that opens a
  popover, OR a Command Palette entry "Show standards mapping for this
  field", OR a per-screen "Show standards" disclosure toggle. Pick
  whichever fits the screen — don't gate it behind a user mode.
- Standards visibility is what makes ProcuLink trustworthy to 30-year
  procurement veterans. Hiding it because "it's complicated" loses
  them.

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
