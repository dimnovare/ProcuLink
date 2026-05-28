# Inbound Email Channel (Postmark) — Agent Report

_Date: 2026-05-28. Additive build #6 from `format-channel-roadmap.md`. No commits._

## What shipped

Four files, no edits to existing code:

- `ProcuLink.Core/Services/Email/IInboundEmailRouter.cs` — interface + records (`InboundEmailPayload`, `InboundAttachment`, `InboundEmailResult`) + small `IParseJobEnqueuer` seam.
- `ProcuLink.Infrastructure/Services/Email/InboundEmailRouter.cs` — implementation.
- `ProcuLink.Api/Controllers/InboundEmailController.cs` — `POST /api/inbound-email/postmark` + `HangfireParseJobEnqueuer` adapter (Api hosts the Hangfire dep; the router stays Hangfire-free in Infrastructure).
- `ProcuLink.Infrastructure.Tests/Services/Email/InboundEmailRouterTests.cs` — 8 tests.

`dotnet build ProcuLink.slnx --no-restore` clean. `dotnet test ProcuLink.slnx --no-restore` 153 passed (8 new).

## Tenant resolution

Recipient `orders@{slug}.proculink.app` → `{slug}` parsed → resolved via config:

```
Inbound:Postmark:TenantMapping:{slug} = "<org-guid>"
Inbound:Postmark:HostSuffix           = ".proculink.app"   # optional
```

Deliberate workaround — `Organisation` has no `OrgSlug` column and the task forbids touching the entity/DbContext. **Founder must add `OrgSlug` in a future migration** (`slug citext UNIQUE` on `organisations`, backfill from name) and swap `ResolveOrgIdFromSlug` from config to DB query. Interface unchanged.

Supplier resolution: prefer `EmailPollingConfig.DefaultSupplierId` (same JSONB slot IMAP polling uses); fall back to the org's oldest non-deleted supplier. From-address matching is a future hook.

## Postmark contract

Consumed: `From`, `OriginalRecipient` → `ToFull[0].Email` → `To`, `Subject`, `Attachments[].Name/.Content (base64)/.ContentType`. Returns **200** on success, **401** on bad/missing `X-Postmark-Server-Token`, **422** on unresolvable tenant or blocked status (so Postmark stops retrying). Rate-limited via existing `EnableRateLimiting("upload")`.

## DI registrations the founder adds to `ProcuLink.Api/Program.cs`

```csharp
builder.Services.AddScoped<IParseJobEnqueuer, HangfireParseJobEnqueuer>();
builder.Services.AddScoped<IInboundEmailRouter, InboundEmailRouter>();
```

Scoped because they consume scoped `ProcuLinkDbContext` and `IOrderService`.

## Tests (8)

happy-path CSV; multi-attachment (CSV+XLSX+PDF); unsupported `.docx` skipped; unknown recipient → failure, zero orders; `read_only` tenant → failure, zero orders; `trial_expired` tenant → failure, zero orders; zero-attachment message → success, empty list; mixed extensions verifies only whitelisted types reach `CreateStubAsync`. Uses `Microsoft.EntityFrameworkCore.InMemory` with the same `Ignore<>` pattern as `EmailSettingsServiceTests`; fakes `IOrderService` + `IParseJobEnqueuer`.

## DNS / public setup the founder still needs

1. Create a Postmark Inbound server; webhook URL `https://api.proculink.app/api/inbound-email/postmark`; copy server token into `Inbound:Postmark:WebhookToken`.
2. **Wildcard MX:** `*.proculink.app  10 inbound.postmarkapp.com.` — one record covers every tenant slug.
3. SPF — only for outbound; skip.
4. Until `OrgSlug` lands, per-tenant entries in `Inbound:Postmark:TenantMapping:{slug}` (Railway env vars).

## SendGrid alternative

If Postmark pricing (~$10/mo + per-message) becomes wrong, SendGrid Inbound Parse is a near drop-in: POSTs `multipart/form-data` instead of JSON. Add `POST /api/inbound-email/sendgrid`, map its body to `InboundEmailPayload`, delegate to the same router. `InboundEmailPayload` is provider-neutral by design.

## Known limitations / next steps

- **`OrgSlug` migration** (founder, ~1 hour) — the only blocker for production launch.
- **Free-text body parsing** deferred (build #5). Today: no-attachment messages are logged and dropped.
- **From-address supplier matching** — future; requires `Supplier.contact_email`.
- **`MessageID` dedupe** via existing `IIdempotencyService` worth adding once a Postmark retry is observed in practice.
- **Account-status gate** blocks `read_only` and `trial_expired`; monthly volume limits still enforced downstream in `ParseOrderJob`.
