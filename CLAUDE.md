# ProcuLink — Claude Code Project Memory

> **Code wins over this file.** Every claim here was checked against the tree on 2026-11-24; where
> you find a disagreement, trust the code and fix the doc. The frontend repo's `CLAUDE.md` carries
> the UI rules, the shell and the billing summary. `STATUS.md` and
> `docs/prompts/2026-07-02-fable5-production-push-master-prompt.md` are order-era history: read them
> for the correction log, never as the current plan. History is `git log` — do not re-execute old
> checklists.

## What this project is

A simple document tool: **any document in → read → checked → transformed → sent**, for
bookkeepers and people who are not good with computers. Benchmark (founder): DocParser — be
better, clearer, simpler, smarter, cheaper, quicker.

- **One way in.** `POST /api/documents` (`InboxController`) takes any file with no flow chosen; it
  lands in the workspace inbox flow (`InboxFlow`) and is read with no setup, then sorted into the
  flow it resembles (`DocumentSort`, remembered in `FlowSortMemory`). A first-of-its-kind document
  asks the person one question (`sort-flows` / `move` / `set-up` / `download` on the same controller).
- **Flows** are the repeat setup: intakes, fields, outputs, destinations, lookups
  (`FlowsController`, `FlowIntakesController`, `FlowOutputsController`, `DestinationsController`,
  `LookupsController`, `DocumentsController`, `FlowFilesController`; jobs `ReadDocumentJob`,
  `SendDocumentJob`, `DeliverDocumentJob`, `FlowPickupJob`, `DocumentDeliverySweepJob`).
- **No document kinds as product structure** (founder, 2026-11-05): no orders-vs-invoices split.
- **Checks never block a send** (founder, 2026-11-06). The one founder-approved exception: on an
  automatic flow a failed `duplicate` check holds the document for a person
  (`SendDocumentJob.ShouldSend`). Values are sent as read.
- **Phone alerts** over Web Push (`PushDevicesController`, `WebPushAlertSender`, `PhoneAlertJob`).

The data model is **direction-agnostic** (sender = document issuer, receiver = recipient).

**Plan 13b (2026-11-13) removed the old purchase-order pipeline.** `OrdersController` is a
read-only archive (list, detail, artifact download); the order write path, its jobs, polling and
sweeps, the order webhooks, the supplier catalog sync, the acceptance gate, the connection-revision
machinery and eight `BillingFeature` members are gone (only `Sso` remains). Every old table,
entity and migration stays — no drop migration — so old orders remain readable. Do not write code
against the deleted names; `git log` has them.

## Current state

- **Production is LIVE**: `proculink.eu` (Vercel) + `api.proculink.eu` (Railway API service
  `ProcuLink` + Worker `aware-amazement` — the Worker is mandatory; nothing is read or delivered
  without it), Neon Postgres, Cloudflare R2, Clerk production instance (`clerk.proculink.eu`).
  Relaunched as the document tool 2026-11-14.
- **Stripe is LIVE** — treat billing as real-money infrastructure: never complete real checkouts in
  testing and never create/edit live Stripe objects without the founder.
- Switches on `DocumentEngineGate` (`DocumentEngine:` options, API **and** Worker):
  `EnabledForAll` / `EnabledOrgIds`, `ScansForAll` / `ScanOrgIds` (photos and scanned PDFs),
  `PeppolForAll` / `PeppolOrgIds` (the Peppol / EN 16931 / XRechnung outputs).
- Plans and specs are written to `docs/superpowers/plans|specs/` in the frontend repo and are not
  committed.

## Repository layout

```
%USERPROFILE%\source\repos\ProcuLink\          ← .NET solution (this repo)
├── CLAUDE.md / STATUS.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev :5223
├── ProcuLink.Core\                  ← Domain models + service interfaces
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2/Local storage, delivery
├── ProcuLink.Transform\             ← Readers, output writers, standards, conformance
├── ProcuLink.Worker\                ← Hangfire jobs host (read, send, deliver, pickups, sweeps)
├── ProcuLink.Transform.Tests\
├── ProcuLink.Infrastructure.Tests\
└── ProcuLink.Api.Tests\

%USERPROFILE%\source\repos\project-proculink\  ← Frontend
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun · Framework: Next.js 15 (App Router)
```

## Tech stack — final

| Layer | Choice |
|---|---|
| Frontend | **Next.js 15 (App Router)** + TypeScript + Tailwind + shadcn/ui |
| Package manager | **bun** — never npm or yarn |
| Auth | **`@clerk/nextjs`** (NOT `@clerk/clerk-react` — different package for Next.js) |
| Clerk middleware | `src/middleware.ts` in the frontend — protects `(app)` routes |
| Frontend data | TanStack Query v5 — in Client Components only |
| API | ASP.NET Core 8 — dev :5223 |
| ORM | EF Core 8 + Npgsql |
| Database | PostgreSQL — `Host=localhost;Port=5435;Database=proculink_dev` |
| File storage | Cloudflare R2 (dev: `LocalFileStorageService` when keys absent) |
| Background jobs | Hangfire + Hangfire.PostgreSql |
| Error tracking | Sentry (backend `Sentry.AspNetCore`, frontend `@sentry/nextjs`) |
| Deployment | Railway (API + Worker) + Vercel (frontend) |

## Key capabilities (verified 2026-11-24)

- **Ways in** (`IntakeKind`): browser upload (`POST /api/documents`, or
  `POST /api/flows/{flowId}/documents` into a chosen flow), the file API
  (`/api/v1/flows/{flowId}/documents`, `plk_` API keys), inbound email (Postmark webhook →
  `InboundEmailRouter`), and pickups (`FlowPickupJob`: IMAP, SFTP, FTP, FTPS, S3).
- **Files read** (`DocumentFormats`): PDF, CSV, XLSX always; DOCX, TXT, XML, cXML, JSON, EDI, X12
  in `More`; photos and scans behind the scan switch (self-hosted no-egress OCR on the Worker).
  A document is one file of up to 10 pages (`ReadLimits`).
- **Outputs** (`FlowOutputFormats`): CSV, JSON, XML, XLSX, PDF, text, fixed-width; plus the
  standards in `ProcuLink.Transform/Documents/StandardOutputs.cs` — cXML 1.2, UBL 2.1 Order,
  X12 850, EDIFACT ORDERS, and (behind the Peppol switch) Peppol BIS Billing 3 invoice / credit note,
  EN 16931 CII and XRechnung UBL. Rule checks for those run on demand through `PhormValidator`
  (the phive sidecar, `Validator:BaseUrl`); an outage never stops a send.
- **Destinations** (`FlowDestination.Protocol`, `DeliveryProtocolConstants`): email, SFTP, FTPS, S3,
  web address (`http`, + OAuth2), Google Drive, Google Sheets — encrypted credentials, SSRF guard
  (`OutboundRequestGuard`), test send, retry. Postmark HTTPS is the email path; SMTP is retired on
  Railway.
- **AI** suggestions through the provider-neutral `IAiMappingService` (OpenAI structured outputs),
  never auto-applied, confidence shown. One AI token allowance per org (`PlanConstants`).
- **Billing**: two ladders in `PlanConstants.cs`, mirrored by the frontend's `src/lib/plans.ts`
  (diffed by `src/test/backendMirror.test.ts` there). The **document ladder** (`DocumentLadder`) is
  the one sold: 30-day trial (`PilotDuration`, 20 documents, 2 flows), Starter €35 / Pro €69 /
  Business €149 / Scale €399 (150 / 400 / 1,500 / 5,000 documents a month; 5 / 20 / 100 /
  unlimited flows; extra documents €0.24 / €0.18 / €0.10 / €0.08, capped by
  `BestPriceOverageOrders`). The **legacy ladder** (Growth / Operations / Integration / Distributor,
  €0.50/order overage) is kept for existing payers only. Paid caps are soft; only the trial's is
  hard. `MinimumPlan` holds one presentation-only row (`Sso` = Enterprise), so nothing is refused by
  plan; `BillingGateEnforcementIsRealTests` (compiled IL) keeps that table honest.
- **Refusals**: intake answers 429 `{ error, plan, limit, upgradeUrl }` with `pilot_expired`,
  `order_limit_reached` or `processing_paused` (`IntakeResults.cs`); flow creation answers
  `flow_limit_reached` (`FlowsController`, `InboxController` set-up).

## Durable coding conventions

### Next.js frontend
- **App Router only** — no Pages Router. Server Components by default; `'use client'` only
  when hooks/events/browser APIs are needed.
- **TanStack Query** for all server state, in Client Components only. No `useEffect` fetching.
- All API calls via `src/lib/api-client.ts` (and the `src/lib/api/` modules it re-exports).
- `@clerk/nextjs`: `auth()` in Server Components, `useAuth()`/`useUser()` in Client Components.
- `next/link` + `next/navigation` — never `react-router-dom`. SEO via `generateMetadata()`.
- Env: `NEXT_PUBLIC_*` prefix for client-visible values, nothing else.
- **bun only.** No Vite, no Lovable-generated code, no `VITE_*` vars.

### .NET backend
- Controllers thin. Every service method takes `Guid organisationId`.
- **All EF queries org-scoped: `.Where(x => x.OrganisationId == organisationId)` — ever, no exceptions.**
- Hangfire jobs idempotent.
- No raw SQL — EF Core only.
- Billing, tenancy, and security code are high-care areas: extra review before merge.
- Every new endpoint needs a frontend caller in the same pair of PRs:
  `EndpointReachabilityGuardTests` fails an endpoint nothing calls.

### One great experience rule
ProcuLink ships ONE great UX — smart defaults + progressive disclosure + Command Palette
(Cmd+K) for power features. No novice/expert mode toggles, no `localStorage` user-mode flags.
Density and power affordances (raw files, hotkeys, standards mappings) surface via the
palette and contextual disclosure (a flow's Advanced view).

### Standards-visibility rule
Any field in a transform/mapping context must be able to surface its standards mapping
(UBL / EDIFACT / X12 / cXML / Peppol BIS) on demand — via info-icon popover, Command Palette,
or a per-screen disclosure. Source of truth: `src/lib/standards/catalog.ts` in the frontend repo
(the conservative, honest capability matrix; the old `/library/standards` page is retired and
308s). Standards visibility is what earns procurement veterans' trust — don't hide it.

## Durable product rules

- **Offer ⇔ works.** Never let UI or marketing offer a channel/format/capability that is not
  a real, tested capability. When in doubt, soften the copy — never over-claim.
  `src/lib/standards/catalog.ts` is the conservative source of truth.
- **No commercial EDI licences** (EdiFabric rejected, ~€1,500/yr). EDIFACT is hand-rolled or
  MIT open-source only.
- **The old Order Workshop is gone** (plan 13b). Old orders are read at `/archive`; the field
  mapping surface is the flow Advanced view (`MapperWorkbench`, `variant="flow"`).
- **Bridge Layer visual direction is locked** (navy/violet/blue→green). The visual canon stays;
  bridge/dock/crossing VOCABULARY is purged from user-facing copy. Only propose a visual-direction
  change with demonstrated superiority + founder sign-off.
- **Plain-language user-facing copy.** No internal jargon (revisions, spine, crossings, dev-rule
  strings) in the UI; errors are one human sentence with what happened + what to do.
- **Legal identity:** the operating entity is **Diip Solutions OÜ** (registry 17527757, Tallinn);
  frontend source of truth `project-proculink/src/lib/legal-entity.ts`. Never restore the
  fabricated "ProcuLink OÜ" identity or invent a VAT number.

## Plugins / skills expected in every session

- **superpowers** — `/superpowers:brainstorm` before any task touching ≥3 files;
  write-plan → execute-plan for medium+ tasks; `/superpowers:debug` after one failed fix.
- **frontend-design** (+ design skills) — all UI work executes the locked design system
  (`docs/design-system/`, read `00-agent-quick-brief.md` first); never invent a new direction.
- **code-review** — run at the end of every task group before merging. Never skip.

## Environment variables

`ProcuLink.Api/appsettings.Development.json` holds the dev shape (local Postgres on `:5435`, the dev
Clerk authority, empty R2/Stripe/AI/Postmark secrets, `Validator:BaseUrl`). Secrets never go in it.
Stripe price ids are `Stripe:<Plan>PriceId` / `Stripe:<Plan>YearlyPriceId` for every checkout plan
(`StripeBillingMapping.cs`). On Railway, read variable NAMES only.

Local live QA needs `PROCULINK_QA_BYPASS_AUTH=true` (Development only), a valid
`Delivery__EncryptionKey` (32-byte base64), `DocumentEngine__EnabledForAll=true`, and the Worker
running (`DOTNET_ENVIRONMENT=Development` for the Worker).

Frontend: `project-proculink/.env` (committed: `NEXT_PUBLIC_API_BASE_URL`, `NEXT_PUBLIC_USE_MOCK=false`,
…) and `.env.local` (gitignored: Clerk keys, Sentry DSN).

## Key links
- Frontend: https://github.com/dimnovare/project-proculink
- Frontend local: %USERPROFILE%\source\repos\project-proculink
- Backend local: %USERPROFILE%\source\repos\ProcuLink
- API dev: http://localhost:5223 · Scalar: /scalar · Hangfire (dev): /hangfire
- Prod: https://proculink.eu · https://api.proculink.eu
- Clerk: https://clerk.com · Neon: https://neon.tech · Cloudflare R2: https://dash.cloudflare.com
- Railway: https://railway.app · Vercel: https://vercel.com · Stripe: https://stripe.com

## Token/context discipline
- **Every session and chip starts in caveman mode and stays in it.** Invoke the `caveman` skill
  first, before anything else — terse prose, no articles/filler/hedging, fragments fine.
  **Prose only:** code, commit messages, PR bodies and user-facing copy stay written normally, as do
  security warnings, confirmations before irreversible actions, and multi-step sequences where
  fragment order could be misread. File:line evidence, exact error strings and command output are
  never abbreviated.
  *Why:* sessions here run long and in parallel, and one that exhausts its context mid-packet leaves
  a branch nobody else can safely pick up.
- Batch independent tool calls into one message. Delegate fan-out reads to subagents — as many in
  parallel as the work genuinely splits into — so they return a conclusion, not a file dump.
- Read only the part of a file you need. Whole-file reads are how a session dies at 40% of the task.
- Read `docs/superpowers/plans/2026-07-27-v1-master-plan/05-PROGRESS.md` before re-investigating
  anything: it carries the correction log and the numbered traps, and most dead ends are already paid
  for once.
- Push anything worth keeping the moment it is committed — a session's worktree is not storage.
- Before dispatching a chip, check the **open PR set**, not just `main`. A prerequisite may be
  written, reviewed and green yet invisible from `main` (TRAP 27). Give each chip a file-disjoint
  scope and name the files that belong to another chip.
- Start with `git status --short` and `git diff --stat`.
- Do not run full `git diff` unless asked.
- Do not read all markdown documentation automatically; read only task-relevant files.
- Use isolated git worktrees for parallel work — never run parallel agents in the shared
  checkout (EF snapshot / `.next` collisions are a known failure mode).
- Windows dev, Linux CI/prod: after pushing, check `gh run list` — local green ≠ CI green.
- Verify infra (Railway variables, Stripe dashboard) before trusting a doc's gap claim —
  docs have historically lagged reality here.

## What NOT to do
- ❌ `npm install` — **bun** only
- ❌ Lovable or Vite/React-Router patterns — App Router + `next/navigation` only
- ❌ `@clerk/clerk-react` — Next.js uses `@clerk/nextjs`
- ❌ `react-helmet-async` — use `generateMetadata()`
- ❌ `VITE_*` env vars — use `NEXT_PUBLIC_*`
- ❌ EF queries without `org_id` scope — ever
- ❌ Raw SQL — EF Core only
- ❌ Hangfire jobs that are not idempotent
- ❌ `useEffect` for data fetching — TanStack Query only
- ❌ Native `window.confirm` — use the shared `useConfirm()` dialog primitive
- ❌ Touch Stripe LIVE mode, rotate secrets, or run destructive prod actions without the founder
- ❌ Hardwire AI mapping to one vendor — keep the provider-neutral `IAiMappingService` seam
- ❌ Skip `/superpowers:brainstorm` for tasks touching ≥3 files, or `/code-review` at group end
- ❌ Ship UI/marketing claims for untested capabilities (offer ⇔ works)
- ❌ **Create a Neon database branch, or re-enable an integration that creates them.** The
  Vercel↔Neon and Neon↔GitHub integrations were removed 2026-07-25 after 22 auto-created
  preview branches accumulated and billed compute; the project now has exactly ONE branch,
  `production`. Use local Postgres (`:5435`) or Testcontainers for tests. If a branch ever
  looks genuinely necessary, ask the founder first and delete it in the same session.
