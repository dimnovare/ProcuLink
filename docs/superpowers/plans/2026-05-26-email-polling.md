# Email Polling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Integration+ organisations ingest buyer order attachments from an IMAP mailbox into the existing upload → parse pipeline.

**Architecture:** Store per-organisation IMAP settings in `organisations.email_config` as JSONB, with passwords encrypted inside the JSON. Add settings endpoints in the API, schedule `EmailPollingJob` from `ProcuLink.Worker`, and reuse `IOrderService.CreateStubAsync` plus `ParseOrderJob` so email attachments follow the same order lifecycle as manual uploads.

**Tech Stack:** .NET 8, EF Core migration, MailKit/MimeKit, Hangfire recurring job every 5 minutes, Next.js App Router/TanStack Query-style settings UI.

---

## File Structure

- `ProcuLink.Core/Entities/Organisation.cs` — add `EmailConfigJson`.
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — map `email_config` as `jsonb`.
- `ProcuLink.Core/Services/Email/EmailSettingsContracts.cs` — request/response/config records.
- `ProcuLink.Core/Services/Email/IEmailSettingsService.cs` — service contract.
- `ProcuLink.Infrastructure/Services/EmailSettingsService.cs` — org-scoped config read/update with encrypted password preservation.
- `ProcuLink.Api/Controllers/SettingsController.cs` — `GET/PUT /api/settings/email`.
- `ProcuLink.Worker/Jobs/EmailPollingJob.cs` — IMAP polling and attachment intake.
- `ProcuLink.Worker/Program.cs` — Hangfire + recurring job + service registrations.
- `ProcuLink.Worker/ProcuLink.Worker.csproj` — MailKit/Hangfire/API reference.
- `project-proculink/src/types/procurement.ts` — email config types.
- `project-proculink/src/lib/api-client.ts` — get/update email settings helpers.
- `project-proculink/src/app/(app)/settings/page.tsx` — real email polling settings section.
- `STATUS.md`, `CLAUDE.md`, `AGENTS.md` — mark Group H complete.

---

### Task 1: Backend Settings Contracts and Tests

- [x] Add failing tests for `EmailSettingsService`:
  - returns default disabled config when none exists;
  - saves config and encrypts password;
  - preserves encrypted password when update omits password;
  - clears password when update passes an empty password.
- [x] Run the service tests and confirm missing-type failures.

### Task 2: Organisation Config and Migration

- [x] Add `EmailConfigJson` to `Organisation`.
- [x] Map `email_config` as `jsonb` with default `{}`.
- [x] Generate EF migration `AddEmailConfigToOrganisations`.
- [x] Run backend build.

### Task 3: Email Settings Service and API

- [x] Implement email settings contracts and `IEmailSettingsService`.
- [x] Implement `EmailSettingsService` with encrypted password preservation.
- [x] Add `SettingsController`:
  - `GET /api/settings/email`;
  - `PUT /api/settings/email`;
  - enabling requires `BillingFeature.EmailIngestion`;
  - selected supplier must belong to current organisation.
- [x] Register service in API DI.
- [x] Run API/infrastructure tests.

### Task 4: Worker IMAP Polling

- [x] Add MailKit and Hangfire packages to `ProcuLink.Worker`.
- [x] Add `EmailPollingJob`:
  - loads enabled org configs;
  - skips orgs without EmailIngestion feature;
  - connects to configured IMAP folder;
  - processes unseen CSV/XLSX/PDF attachments;
  - creates order stubs through `IOrderService.CreateStubAsync`;
  - enqueues `ParseOrderJob`;
  - marks processed messages as seen.
- [x] Wire Worker DI and recurring Hangfire schedule every 5 minutes.
- [x] Run solution build.

### Task 5: Frontend Email Settings UI

- [x] Add email settings types and API helpers.
- [x] Replace the Email polling placeholder with a restrained Bridge Layer config panel:
  - enabled toggle;
  - host/port/SSL/folder;
  - username/password;
  - default supplier dropdown;
  - masked saved password state;
  - billing-gate message for non-Integration plans.
- [x] Run `bun run build`.

### Task 6: Handoff and Commit

- [x] Update `STATUS.md`, `CLAUDE.md`, and `AGENTS.md`.
- [x] Run:
  - `dotnet build ProcuLink.slnx --no-restore`
  - `dotnet test ProcuLink.slnx --no-restore`
  - `bun run build`
- [x] Commit backend and frontend separately.
- [x] Push if clean and verified.

---

## Scope Notes

- IMAP polling ingests order attachments only; body-only plain-text email parsing is deferred.
- Attachments use the existing parser support: CSV, XLSX, and text-based PDF.
- The first implementation marks inspected messages as seen to avoid duplicate imports; richer dedupe can be added with message-id tracking later.
