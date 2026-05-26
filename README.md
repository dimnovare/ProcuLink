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

## Local Backend

Default development API:

```text
http://localhost:5223
```

Useful local URLs:

```text
Scalar UI:          http://localhost:5223/scalar
Hangfire dashboard: http://localhost:5223/hangfire
```

Run backend:

```bash
dotnet run --project ProcuLink.Api
```

Run worker:

```bash
dotnet run --project ProcuLink.Worker
```

Run tests:

```bash
dotnet test ProcuLink.slnx --no-restore
```

Build:

```bash
dotnet build ProcuLink.slnx --no-restore
```

## Configuration

Development settings live in:

```text
ProcuLink.Api\appsettings.Development.json
```

Required local database shape:

```text
Host=localhost;Port=5435;Database=proculink_dev;Username=postgres;Password=postgres
```

Frontend development variables use Next.js names, not Vite names:

```text
NEXT_PUBLIC_API_BASE_URL=http://localhost:5223
NEXT_PUBLIC_USE_MOCK=false
```

Do not commit real Stripe, Clerk, OpenAI, R2, or delivery credential secrets.

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

## Next Work

The recommended next sequence is:

1. UI/UX production polish and mobile responsiveness.
2. Live end-to-end QA for Clerk, Stripe, upload, mapping, transform, delivery, ERP, and IMAP.
3. Engine hardening for more standards and output templates.
4. Trust/commercial readiness: onboarding, support/legal, analytics, copy, and proof points.

## License

Private - All rights reserved.
