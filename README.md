# ProcuLink

ProcuLink is a B2B outbound procurement bridge for buyer/procurement teams that
need to turn messy purchase-order sources into supplier-ready outputs.

Core workflow:

```text
Parse -> Normalize -> Validate -> Review exceptions -> Transform -> Deliver -> Learn
```

The product is no longer a file-based prototype. Treat this repository as the
current .NET backend/worker solution for a production SaaS.

## Repositories

```text
ProcuLink/                   (this repo)
  ProcuLink.Api              ASP.NET Core 8 API
  ProcuLink.Core             Domain models, constants, service interfaces
  ProcuLink.Infrastructure   EF Core, PostgreSQL, storage, delivery services
  ProcuLink.Transform        CSV/XLSX/PDF parsing and output transforms
  ProcuLink.Worker           Hangfire worker jobs

project-proculink/           (sibling repo — checked out alongside this one)
  Next.js 15 App Router frontend
```

Frontend repo: https://github.com/dimnovare/project-proculink

## Current Stack

- Backend: ASP.NET Core 8
- ORM/database: EF Core 8 + PostgreSQL
- Jobs: Hangfire + Hangfire.PostgreSql
- Storage: Cloudflare R2, with local storage fallback in development
- Auth: Clerk JWT validation, plus `plk_` API-key scheme for machine-to-machine ingress
- Billing: Stripe
- AI mapping: provider-neutral interface, OpenAI structured outputs first
- Standards: cXML 1.2, UBL 2.1 (Peppol BIS 3.0-compatible), EDIFACT, ANSI X12 850
- Delivery channels: HTTP/webhook, SFTP, FTPS, SMTP, Erply/Directo ERP
- Frontend: Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, TanStack Query
- Package manager: bun only for frontend

## Local Setup

### Prerequisites

- .NET 8 SDK
- Docker Desktop (for Postgres)
- (optional) `dotnet-ef` CLI: `dotnet tool install --global dotnet-ef`

### One-time setup

**1. Trust the dev HTTPS certificate.** The frontend talks to `https://localhost:7230` by default. Without this, browser fetch calls fail silently with `Failed to fetch`.

```bash
dotnet dev-certs https --trust
```

**2. Start Postgres** on the expected port (5435):

```bash
docker compose up -d postgres
```

**3. Apply EF migrations:**

```bash
dotnet ef database update --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

**4. (Optional) Local secrets** via `dotnet user-secrets` from the `ProcuLink.Api` folder:

```bash
cd ProcuLink.Api
dotnet user-secrets set "Ai:OpenAI:ApiKey" "sk-..."
dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."
```

Without these, AI mapping and Stripe billing no-op silently — the rest of the stack works.

### Run the stack

Open **three terminals**:

**Terminal 1 — API** (HTTPS profile opens both `:7230` and `:5223`):

```bash
dotnet run --project ProcuLink.Api --launch-profile https
```

| URL | What |
|---|---|
| `https://localhost:7230/health` | Health check |
| `https://localhost:7230/scalar` | API explorer |
| `https://localhost:7230/hangfire` | Job dashboard |

**Terminal 2 — Worker** (Hangfire processor):

```bash
dotnet run --project ProcuLink.Worker
```

**Terminal 3 — Frontend** (sibling repo):

```bash
cd ../project-proculink
bun install
bun run dev
```

Frontend at `http://localhost:8082`. Ensure its `.env.local` has:

```text
NEXT_PUBLIC_API_BASE_URL=https://localhost:7230
```

If you prefer HTTP only, use `--launch-profile http` for the API and set `NEXT_PUBLIC_API_BASE_URL=http://localhost:5223`. The CORS allow-list (`Program.cs`) includes both ports.

### Verify

1. `https://localhost:7230/health` returns `Healthy`.
2. `http://localhost:8082` loads the marketing landing page.
3. Sign in → onboarding wizard appears → adding a supplier succeeds (no `Failed to fetch`).

### Common issues

| Symptom | Cause | Fix |
|---|---|---|
| `Failed to fetch` on every API call | Dev HTTPS cert not trusted | `dotnet dev-certs https --trust`, then restart the browser |
| `Failed to fetch` on POST requests only | CORS preflight blocked | Confirm the frontend port (default 8082) is in `Program.cs` CORS allow-list, or add it via `Frontend:Url` env var |
| `connection refused` on `localhost:5435` | Postgres not running | `docker compose up -d postgres` |
| `relation "..." does not exist` | Migrations not applied | `dotnet ef database update --project ProcuLink.Infrastructure --startup-project ProcuLink.Api` |
| Hangfire jobs never run | Worker not started | Start Terminal 2 |
| AI suggestions never appear | No OpenAI key | `dotnet user-secrets set "Ai:OpenAI:ApiKey" "sk-..."` |
| Stripe billing UI hangs | No Stripe key + no graceful fallback in dev | Set a Stripe test key in user-secrets, or stay off the billing screens |

## Build + test

```bash
dotnet build ProcuLink.slnx --no-restore
dotnet test ProcuLink.slnx --no-restore
```

Current baseline: **272 tests** (123 Transform + 21 Api.Tests + 128 Infrastructure), 0 failures.

## Configuration files

| File | Purpose |
|---|---|
| `ProcuLink.Api/appsettings.Development.json` | Dev defaults (Postgres connection, Clerk authority, plan price ids stubs) |
| `ProcuLink.Api/appsettings.Production.json` | Empty production placeholders — values come from Railway env vars |
| `docker-compose.yml` | Postgres 15 on port 5435 |

Do not commit real Stripe, Clerk, OpenAI, R2, or delivery credential secrets — use user-secrets or env vars.

## Current Product State

Implemented baseline:

- Auth, PostgreSQL tenancy, and core order loop
- Final billing ladder: Pilot, Growth, Operations, Integration, Enterprise
- PO field mapping engine with magic mapping preview (source → canonical → supplier, with AI suggestions, confidence + provenance, accept/edit/reject before commit)
- AI mapping suggestions (provider-neutral, OpenAI structured outputs first)
- Input parsers: CSV/XLSX, text-based PDF (text→LLM extraction via PdfPig + OpenAI, with number-vs-source validation; deterministic column-parser fallback when no key/offline), scanned/image-only PDF (AI vision fallback — rasterize via PDFtoImage + SkiaSharp → vision-capable OpenAI model; every line flagged for human review), cXML 1.2, UBL 2.1, EDIFACT, ANSI X12 850
- Self-hosted no-egress OCR (opt-in, enterprise/operator-enabled): RapidOcrNet (PP-OCRv5 via ONNX Runtime, Apache-2.0 code + weights, in-process, no GPU, no external network calls) implementing the `IDocumentOcrService` seam. Requires global `NoEgressOcr:Enabled=true` on the API + Worker AND per-org `Organisation.SelfHostedOcr=true`. For a no-egress org the whole ingest/parse pipeline avoids OpenAI: PDFs route to the deterministic parser with scanned pages OCR'd locally, while AI SKU mapping, email-body NLP, and AI schema inference are all gated → human review / manual mapping. Default prod deploy ships dormant (no-op OCR) until an operator enables it; scanned lines are still assisted/review-flagged and illegible scans still fail. Non-no-egress orgs keep the OpenAI text/vision PDF path (EU-residency project + DPA + zero-retention still required).
- Smart file-format auto-detect (`POST /api/upload/detect-format`)
- Output transforms: CSV/XLSX, cXML, UBL 2.1 (Peppol BIS 3.0-compatible), X12 850
- Supplier delivery channels: HTTP/webhook, SFTP, FTPS, SMTP, Erply/Directo ERP
- HMAC-verified inbound webhook ingress with replay protection
- IMAP email polling for CSV/XLSX/PDF order attachments
- Invoice + ASN canonical models (UBL 2.1 invoice parser; CSV/XML/JSON invoice transforms)
- Zapier/Make.com integration layer: `plk_` API keys, org slug, integration subscriptions, signed trigger firing
- In-app standards comparison screen + per-field standards popovers (UBL / EDIFACT / X12 / cXML refs on demand)

Read [STATUS.md](./STATUS.md) before starting new work. It is the current handoff
source of truth.

## Design And Frontend Direction

Frontend implementation lives in `project-proculink`, not this repo. The frontend
is a native Next.js 15 App Router application.

Current rules:

- No Vite, no `VITE_*` env vars, no `react-router-dom`
- No Lovable-generated code
- Use the local design system in `docs/design-system`
- Read `docs/design-system/00-agent-quick-brief.md` first for UI work
- Locked visual direction: Direction 4, The Bridge Layer, supported by Direction 3, System Identity

## Roadmap — Phase 6: International Standard

ProcuLink's product thesis is to become the **international standard for
outbound B2B purchase order routing**: any input format / channel → canonical
PO → any output format / channel. Depth for 30-year procurement veterans,
effortless for first-time users, cost-effective versus SPS Commerce /
TrueCommerce / Babelway / Pagero.

Source of truth for the forward plan:

```text
docs\superpowers\plans\2026-05-28-phase-6-international-standard-roadmap.md
```

| Horizon | Theme | Timeline |
|---|---|---|
| 1 | Production Ready + Effortless | next 4–6 weeks |
| 2 | Standards Backbone + Channel Breadth | Q4 2026 |
| 3 | Network Effects | Q1 2027+ |

Shipped to `main` (2026-05-29): UBL 2.1 outbound transformer, ANSI X12 850
parser/transformer, SFTP + FTPS + SMTP delivery, HMAC webhook ingress, smart
format auto-detect, magic mapping preview, and the standards comparison screen.
Earlier groups (Stripe billing, PO mapping engine, AI suggestions, PDF
ingestion, ERP connectors, IMAP polling, cXML 1.2, Invoice/ASN models,
Zapier/Make.com layer) are all complete.

**UX direction:** ProcuLink ships ONE great experience — smart defaults +
progressive disclosure + a Command Palette (Cmd+K) for power features. The
earlier "default vs expert mode" toggle was dropped before adoption. Standards
visibility surfaces via info popovers and the Command Palette, never behind a
user-mode flag.

Remaining before production launch is largely founder configuration (PostHog
keys, Clerk post-signup redirect, status/Loom/demo URLs, optional SMTP) plus
live deployed end-to-end QA. See [STATUS.md](./STATUS.md).

## License

Private - All rights reserved.
