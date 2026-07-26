# ProcuLink — Claude Code Project Memory

> **Current source of truth:** read `STATUS.md` (repo root) before planning — it overrides
> anything stale here. The active work plan + verified capability ground truth is
> [`docs/prompts/2026-07-02-fable5-production-push-master-prompt.md`](docs/prompts/2026-07-02-fable5-production-push-master-prompt.md).
> Implementation history (phases, groups, waves): see `git log` and STATUS.md — do not
> re-execute old checklists.

## What this project is

ProcuLink is a **B2B procurement bridge**: it moves purchase orders between buyers and
suppliers in each counterparty's required format and delivery channel.

**Import an order source → parse → normalize → validate → review exceptions → transform → deliver → learn.**

The data model is **direction-agnostic** (buyer = document issuer, supplier = recipient);
each org chooses its direction (outbound "send POs to suppliers" is the default, inbound
"receive customer POs" is a per-org setting that relabels the UI — display-only).

## Current direction (2026-07)

- **Production is LIVE** (launched 2026-06-09): `proculink.eu` (Vercel) + `api.proculink.eu`
  (Railway API service `ProcuLink` + single Worker `aware-amazement` — the Worker is mandatory;
  nothing parses/delivers without it), Neon Postgres, Cloudflare R2, Clerk production instance
  (`clerk.proculink.eu`). **Stripe is LIVE** (verified 2026-07-02 via API: `sk_live` key,
  all 8 monthly+yearly price IDs active) — treat billing as real-money infrastructure:
  never complete real checkouts in testing and never create/edit live Stripe objects
  without the founder.
- **Active work:** the production-hardening push in
  `docs/prompts/2026-07-02-fable5-production-push-master-prompt.md` — prove every advertised
  capability live, click-audit the UI, consolidate design drift, make marketing truthful.
- **Product north star:** the **versioned Supplier Connection** — mappings/rules/templates/
  delivery/catalog unified in one first-class versioned object (draft → test → publish →
  archive), every order pinning a `ConnectionRevisionId` for reproducibility, replay, and
  rollback. Shipped and live; keep new connection concepts first-class (no `CanonicalJson`
  overloading), keep Scriban as the power-user escape hatch (not the default), and never
  equate HTTP 200 with supplier business acceptance.

## Repository layout

```
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\          ← .NET solution (this repo)
├── CLAUDE.md / STATUS.md
├── ProcuLink.slnx
├── ProcuLink.Api\                   ← ASP.NET Core 8 — dev :5223
├── ProcuLink.Core\                  ← Domain models + service interfaces
├── ProcuLink.Infrastructure\        ← EF Core, Postgres, R2/Local storage, delivery
├── ProcuLink.Transform\             ← Format parsers + output transforms
├── ProcuLink.Worker\                ← Hangfire jobs host (parse/deliver/polling)
├── ProcuLink.Transform.Tests\
├── ProcuLink.Infrastructure.Tests\
└── ProcuLink.Api.Tests\

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\  ← Frontend
GitHub: https://github.com/dimnovare/project-proculink
Package manager: bun · Framework: Next.js 15 (App Router)
```

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
| Deployment | Railway (API + Worker) + Vercel (frontend) |

## Key capabilities (verified; detail in the master prompt Appendix A)

- **Input formats:** CSV, XLSX, PDF (text→LLM primary; vision fallback for scanned; regex +
  self-hosted no-egress OCR fallbacks), UBL 2.1 / Peppol BIS 3 Order, cXML 1.2,
  EDIFACT ORDERS (D96A/D01B, hand-rolled), X12 850, SAP IDoc ORDERS05. UBL Invoice parses;
  EDIFACT INVOIC/DESADV are **stubs**.
- **Output formats:** XML, CSV, JSON, cXML, UBL (Peppol BIS Order 3), X12 850 — plus the
  `OutputNode` AST / output designer and Scriban escape hatch.
- **Delivery channels:** `http` (+OAuth2), `sftp`, `ftps`, `email` (Postmark HTTPS is the
  canonical email path; SMTP retired on Railway), `erp_erply`, `erp_directo` — all with
  AES-GCM credentials, SSRF guard (`OutboundRequestGuard`), test-fire, retry/dead-letter.
- **Ingress channels:** browser upload, REST ingress (`plk_` API keys), inbound email
  (`{slug}@orders.proculink.eu` via CF MX → Postmark), IMAP polling, SFTP/S3 polling,
  catalog import (API push + SFTP/FTP(S) + HTTP pull).
- AI mapping suggestions (provider-neutral `IAiMappingService`, OpenAI structured outputs,
  never auto-applied, confidence + provenance shown), schema fingerprinting, acceptance
  profiles, admin area, billing with soft caps + €0.50/order overage (never hard-blocks paid).

## Durable coding conventions

### Next.js frontend
- **App Router only** — no Pages Router. Server Components by default; `'use client'` only
  when hooks/events/browser APIs are needed.
- **TanStack Query** for all server state, in Client Components only. No `useEffect` fetching.
- All API calls via `src/lib/api-client.ts`.
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

### One great experience rule
ProcuLink ships ONE great UX — smart defaults + progressive disclosure + Command Palette
(Cmd+K) for power features. No novice/expert mode toggles, no `localStorage` user-mode flags.
Density and power affordances (raw envelopes, hotkeys, standards mappings) surface via the
palette, per-table column selectors, and contextual disclosure.

### Standards-visibility rule
Any field in a transform/mapping context must be able to surface its standards mapping
(UBL / EDIFACT / X12 / cXML / Peppol BIS) on demand — via info-icon popover, Command Palette,
or a per-screen disclosure. Source of truth: the in-app **`/library/standards`** catalog,
backed by `src/lib/standards/catalog.ts` in the frontend repo (the conservative,
honest capability matrix). Standards visibility is what earns 30-year procurement veterans'
trust — don't hide it.

## Durable product rules

- **Offer ⇔ works.** Never let UI or marketing offer a channel/format/capability that is not
  a real, tested capability. When in doubt, soften the copy — never over-claim.
  `/library/standards` + `src/lib/standards/catalog.ts` are the conservative source of truth.
- **No commercial EDI licences** (EdiFabric rejected, ~€1,500/yr). EDIFACT is hand-rolled or
  MIT open-source only. EDIFACT INVOIC/DESADV remain stubs — present as "coming soon", never
  as errors.
- **The 3-column Order Workshop layout is locked.** `/inbox/[orderId]` renders `OrderWorkshop`;
  polish and fix, do not restructure or replace the layout.
- **Bridge Layer visual direction is locked** (navy/violet/blue→green). The visual canon stays;
  bridge/dock/crossing VOCABULARY is purged from user-facing copy. Only propose a visual-direction
  change with demonstrated superiority + founder sign-off.
- **Plain-language user-facing copy.** No internal jargon (revisions, spine, crossings, dev-rule
  strings) in the UI; validation errors are one human sentence with actual-vs-expected + fix.
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

Local live QA needs `PROCULINK_QA_BYPASS_AUTH=true` (Development only) + a valid
`Delivery__EncryptionKey` (32-byte base64), and the Worker running.

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

## Key links
- Frontend: https://github.com/dimnovare/project-proculink
- Frontend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
- Backend local: C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
- API dev: http://localhost:5223 · Scalar: /scalar · Hangfire (dev): /hangfire
- Prod: https://proculink.eu · https://api.proculink.eu
- Clerk: https://clerk.com · Neon: https://neon.tech · Cloudflare R2: https://dash.cloudflare.com
- Railway: https://railway.app · Vercel: https://vercel.com · Stripe: https://stripe.com

## Token/context discipline
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
