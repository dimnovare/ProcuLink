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
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
  ProcuLink.Api              ASP.NET Core 8 API
  ProcuLink.Core             Domain models, constants, service interfaces
  ProcuLink.Infrastructure   EF Core, PostgreSQL, storage, delivery services
  ProcuLink.Transform        CSV/XLSX/PDF parsing and output transforms
  ProcuLink.Worker           Hangfire worker jobs

C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
  Next.js 15 App Router frontend
```

Frontend repo: https://github.com/dimnovare/project-proculink

## Current Stack

- Backend: ASP.NET Core 8
- ORM/database: EF Core 8 + PostgreSQL
- Jobs: Hangfire + Hangfire.PostgreSql
- Storage: Cloudflare R2, with local storage fallback in development
- Auth: Clerk JWT validation
- Billing: Stripe
- AI mapping: provider-neutral interface, OpenAI structured outputs first
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

Current baseline: **213 tests** (102 Transform + 11 Api.Tests + 100 Infrastructure), 0 failures.

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
- PO field mapping engine
- Supplier delivery configuration, HTTP-first
- AI mapping suggestions
- Text-based PDF ingestion
- ERP delivery adapters for Erply and Directo
- IMAP email polling for CSV/XLSX/PDF order attachments

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

## Phase 5 Roadmap

The grouped roadmap is documented in:

```text
docs\superpowers\plans\2026-05-26-production-hardening-roadmap.md
```

Next group:

```text
Group I — UI/UX production polish + responsive QA
```

Then:

- Group J — Live end-to-end QA + deployment hardening
- Group K — Standards + engine hardening
- Group L — Trust, onboarding + commercial readiness

## License

Private - All rights reserved.
