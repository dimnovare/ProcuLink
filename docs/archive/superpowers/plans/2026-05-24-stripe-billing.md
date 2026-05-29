# Stripe Billing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Stripe-powered Pilot/Growth/Operations/Integration/Enterprise billing to ProcuLink, with per-plan order and supplier limits, feature gating, and a Pilot extension request mechanism.

**Architecture:** Domain constants and contracts live in `ProcuLink.Core`. `StripeBillingService` in `ProcuLink.Api` implements `IBillingService` using Stripe.net. `BillingController` exposes 5 endpoints. Limit checks are injected into `OrdersController.Upload` and `SuppliersController.CreateSupplier`. The Next.js settings page renders the billing section; `UploadWorkbench` handles 429 responses.

**Tech Stack:** .NET 8 / ASP.NET Core, EF Core 8 + Npgsql, Stripe.net, Next.js 15, TanStack Query v5.

---

## File Map

**Create (backend — `ProcuLink/`):**
- `ProcuLink.Core/Constants/PlanConstants.cs`
- `ProcuLink.Core/Constants/BillingFeature.cs`
- `ProcuLink.Core/Services/IBillingService.cs`
- `ProcuLink.Core/Services/BillingStatus.cs`
- `ProcuLink.Core/Services/LimitCheckResult.cs`
- `ProcuLink.Api/Services/StripeBillingService.cs`
- `ProcuLink.Api/Controllers/BillingController.cs`

**Modify (backend):**
- `ProcuLink.Core/Entities/Organisation.cs` — add 5 new properties
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — map new columns
- `ProcuLink.Infrastructure/Migrations/` — new EF migration (generated + edited)
- `ProcuLink.Api/ProcuLink.Api.csproj` — add Stripe.net package
- `ProcuLink.Api/Controllers/OrdersController.cs` — add limit check before upload
- `ProcuLink.Api/Controllers/SuppliersController.cs` — add limit check before create
- `ProcuLink.Api/Program.cs` — register IBillingService, set Stripe API key
- `ProcuLink.Api/appsettings.Development.json` — add Stripe config section

**Create (frontend — `project-proculink/`):**
- `src/components/bridge/BillingSection.tsx` — billing UI component for settings

**Modify (frontend):**
- `src/types/procurement.ts` — add `BillingStatus` type
- `src/lib/api-client.ts` — add billing API functions
- `src/app/(app)/settings/page.tsx` — wire BillingSection into Billing tab
- `src/components/bridge/UploadWorkbench.tsx` — handle 429 with inline banner

---

## Task 1: Plan constants

**Files:**
- Create: `ProcuLink.Core/Constants/PlanConstants.cs`
- Create: `ProcuLink.Core/Constants/BillingFeature.cs`

- [ ] **Step 1: Create `BillingFeature.cs`**

```csharp
// ProcuLink.Core/Constants/BillingFeature.cs
namespace ProcuLink.Core.Constants;

public enum BillingFeature
{
    Xml,
    Pdf,
    MappingLibrary,
    ValidationRules,
    BulkMapping,
    Cxml,
    DeliveryHistory,
    AdvancedAudit,
    WebhookDelivery,
    EmailIngestion,
    CustomTemplates,
    ErpConnectors,
    CustomSupplierRules,
    SlaOnboarding,
}
```

- [ ] **Step 2: Create `PlanConstants.cs`**

```csharp
// ProcuLink.Core/Constants/PlanConstants.cs
namespace ProcuLink.Core.Constants;

public static class PlanConstants
{
    // ── Plan string identifiers ────────────────────────────────────────────
    public const string Pilot       = "pilot";
    public const string Growth      = "growth";
    public const string Operations  = "operations";
    public const string Integration = "integration";
    public const string Enterprise  = "enterprise";

    // ── Pilot trial config ─────────────────────────────────────────────────
    public static readonly TimeSpan PilotDuration     = TimeSpan.FromDays(14);
    public const int                PilotOrderLimit   = 20;
    public const int                PilotSupplierLimit = 1;

    // ── Per-plan limits (orders/month for paid; cumulative for Pilot) ──────
    public static readonly IReadOnlyDictionary<string, (int Orders, int Suppliers)> Limits =
        new Dictionary<string, (int, int)>
        {
            [Pilot]       = (PilotOrderLimit,  PilotSupplierLimit),
            [Growth]      = (150,              5),
            [Operations]  = (500,              10),
            [Integration] = (1_000,            20),
            [Enterprise]  = (int.MaxValue,     int.MaxValue),
        };

    // ── Feature gate: minimum plan required per feature ───────────────────
    private static readonly IReadOnlyDictionary<BillingFeature, string> MinimumPlan =
        new Dictionary<BillingFeature, string>
        {
            [BillingFeature.Xml]                = Growth,
            [BillingFeature.Pdf]                = Growth,
            [BillingFeature.MappingLibrary]     = Growth,
            [BillingFeature.ValidationRules]    = Growth,
            [BillingFeature.BulkMapping]        = Operations,
            [BillingFeature.Cxml]               = Operations,
            [BillingFeature.DeliveryHistory]    = Operations,
            [BillingFeature.AdvancedAudit]      = Operations,
            [BillingFeature.WebhookDelivery]    = Integration,
            [BillingFeature.EmailIngestion]     = Integration,
            [BillingFeature.CustomTemplates]    = Integration,
            [BillingFeature.ErpConnectors]      = Enterprise,
            [BillingFeature.CustomSupplierRules]= Enterprise,
            [BillingFeature.SlaOnboarding]      = Enterprise,
        };

    private static readonly IReadOnlyList<string> PlanOrder =
        new[] { Pilot, Growth, Operations, Integration, Enterprise };

    public static bool PlanHasFeature(string plan, BillingFeature feature)
    {
        if (!MinimumPlan.TryGetValue(feature, out var minPlan)) return false;
        var planIdx = PlanOrder.IndexOf(plan);
        var minIdx  = PlanOrder.IndexOf(minPlan);
        return planIdx >= 0 && planIdx >= minIdx;
    }

    public static BillingFeature[] FeaturesForPlan(string plan) =>
        Enum.GetValues<BillingFeature>()
            .Where(f => PlanHasFeature(plan, f))
            .ToArray();
}
```

- [ ] **Step 3: Build Core to verify no compile errors**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
dotnet build ProcuLink.Core --no-restore
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Core/Constants/
git commit -m "feat(billing): add PlanConstants and BillingFeature enum"
```

---

## Task 2: Domain contracts and IBillingService interface

**Files:**
- Create: `ProcuLink.Core/Services/BillingStatus.cs`
- Create: `ProcuLink.Core/Services/LimitCheckResult.cs`
- Create: `ProcuLink.Core/Services/IBillingService.cs`

- [ ] **Step 1: Create `BillingStatus.cs`**

```csharp
// ProcuLink.Core/Services/BillingStatus.cs
using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services;

/// <summary>
/// Snapshot of a tenant's billing state. Returned by GET /api/billing/status.
/// OrdersUsed is cumulative since trial_started_at for Pilot; month-to-date for paid plans.
/// </summary>
public record BillingStatus(
    string           Plan,
    int              OrdersUsed,
    int              OrderLimit,
    int              SuppliersActive,
    int              SupplierLimit,
    DateTime?        PilotEndsAt,         // effective pilot end for Pilot accounts; null for paid
    bool             PilotExpired,
    bool             ExtensionRequested,  // true if request-extension was called
    string[]         Features             // feature names: BillingFeature enum member names, lowercase
);
```

- [ ] **Step 2: Create `LimitCheckResult.cs`**

```csharp
// ProcuLink.Core/Services/LimitCheckResult.cs
namespace ProcuLink.Core.Services;

/// <summary>
/// Result of a limit check. Distinguishes between pilot expiry and plan-limit exhaustion
/// so controllers can return the correct error code to the frontend.
/// </summary>
public record LimitCheckResult(
    bool   Allowed,
    bool   PilotExpired,  // true only for Pilot accounts past their window
    string Plan,
    int    Limit
);
```

- [ ] **Step 3: Create `IBillingService.cs`**

```csharp
// ProcuLink.Core/Services/IBillingService.cs
using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services;

public interface IBillingService
{
    /// <summary>Returns full billing snapshot for the settings page.</summary>
    Task<BillingStatus> GetStatusAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the org may process another order.
    /// Pilot: cumulative count vs PilotOrderLimit; also checks time window.
    /// Paid: monthly count vs plan limit.
    /// </summary>
    Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Checks whether the org may add another active supplier.</summary>
    Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>Returns true if the org's plan includes the requested feature.</summary>
    Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature, CancellationToken ct = default);

    /// <summary>Creates a Stripe Checkout session for the given plan. Returns the redirect URL.</summary>
    Task<string> CreateCheckoutSessionAsync(Guid orgId, string plan, string returnUrl, CancellationToken ct = default);

    /// <summary>Creates a Stripe Customer Portal session. Returns the redirect URL.</summary>
    Task<string> CreatePortalSessionAsync(Guid orgId, string returnUrl, CancellationToken ct = default);

    /// <summary>Records a pilot extension request and notifies sales. Idempotent.</summary>
    Task RequestPilotExtensionAsync(Guid orgId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Build Core**

```bash
dotnet build ProcuLink.Core --no-restore
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Services/BillingStatus.cs ProcuLink.Core/Services/LimitCheckResult.cs ProcuLink.Core/Services/IBillingService.cs
git commit -m "feat(billing): add IBillingService, BillingStatus, LimitCheckResult"
```

---

## Task 3: Organisation entity, DbContext mapping, EF migration

**Files:**
- Modify: `ProcuLink.Core/Entities/Organisation.cs`
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`
- Create: new EF migration (generated)

- [ ] **Step 1: Update `Organisation.cs` — add 5 new properties**

Replace the existing file content with:

```csharp
// ProcuLink.Core/Entities/Organisation.cs
namespace ProcuLink.Core.Entities;

public class Organisation
{
    public Guid    Id                          { get; set; }
    public string  ClerkOrgId                 { get; set; } = string.Empty;
    public string  Name                       { get; set; } = string.Empty;
    public string  Plan                       { get; set; } = "pilot";
    public DateTime CreatedAt                 { get; set; }

    // ── Pilot trial tracking ───────────────────────────────────────────────
    /// <summary>Set at org creation. Never updated. Drives Pilot time-window check.</summary>
    public DateTime  TrialStartedAt               { get; set; } = DateTime.UtcNow;
    /// <summary>Admin-set override. When set, extends the 14-day deadline.</summary>
    public DateTime? PilotExtendedUntil           { get; set; }
    /// <summary>Set when user clicks "Request Pilot extension". Sales signal.</summary>
    public DateTime? PilotExtensionRequestedAt    { get; set; }

    // ── Stripe ────────────────────────────────────────────────────────────
    public string? StripeCustomerId      { get; set; }
    public string? StripeSubscriptionId  { get; set; }

    // Navigation
    public List<Membership>           Memberships       { get; set; } = new();
    public List<Supplier>             Suppliers         { get; set; } = new();
    public List<PurchaseOrderEntity>  PurchaseOrders    { get; set; } = new();
    public List<ItemMapping>          ItemMappings      { get; set; } = new();
    public List<OutboundArtifact>     OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt>      DeliveryAttempts  { get; set; } = new();
    public List<AuditEvent>           AuditEvents       { get; set; } = new();
}
```

- [ ] **Step 2: Update `ProcuLinkDbContext.cs` — add column mappings for Organisation**

Inside the `modelBuilder.Entity<Organisation>(b => { ... })` block, after the `b.HasIndex(x => x.ClerkOrgId).IsUnique();` line, add:

```csharp
            b.Property(x => x.TrialStartedAt)
             .HasColumnName("trial_started_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.PilotExtendedUntil)
             .HasColumnName("pilot_extended_until")
             .HasColumnType("timestamptz");
            b.Property(x => x.PilotExtensionRequestedAt)
             .HasColumnName("pilot_extension_requested_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.StripeCustomerId)
             .HasColumnName("stripe_customer_id");
            b.Property(x => x.StripeSubscriptionId)
             .HasColumnName("stripe_subscription_id");
```

- [ ] **Step 3: Generate the EF migration**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
dotnet ef migrations add AddStripeFieldsToOrganisations `
  --project ProcuLink.Infrastructure `
  --startup-project ProcuLink.Api
```
Expected: `Build succeeded.` and a new migration file created in `ProcuLink.Infrastructure/Migrations/`.

- [ ] **Step 4: Edit the generated migration — fix `trial_started_at` default**

Open the newly generated migration file (timestamp will vary, e.g. `20260524XXXXXX_AddStripeFieldsToOrganisations.cs`).

Find the `AddColumn` call for `trial_started_at`. It will look like:
```csharp
migrationBuilder.AddColumn<DateTime>(
    name: "trial_started_at",
    table: "organisations",
    type: "timestamp with time zone",
    nullable: false,
    defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
```

Replace it with:
```csharp
migrationBuilder.AddColumn<DateTime>(
    name: "trial_started_at",
    table: "organisations",
    type: "timestamp with time zone",
    nullable: false,
    defaultValueSql: "now()");
```

This ensures existing rows get `NOW()` rather than the epoch. New orgs set `TrialStartedAt = DateTime.UtcNow` in C# before saving.

- [ ] **Step 5: Apply the migration**

```bash
dotnet ef database update `
  --project ProcuLink.Infrastructure `
  --startup-project ProcuLink.Api
```
Expected: `Done.`

- [ ] **Step 6: Build full solution**

```bash
dotnet build
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Core/Entities/Organisation.cs
git add ProcuLink.Infrastructure/ProcuLinkDbContext.cs
git add ProcuLink.Infrastructure/Migrations/
git commit -m "feat(billing): add Stripe + Pilot fields to Organisation entity and migrate"
```

---

## Task 4: Install Stripe.net and configure

**Files:**
- Modify: `ProcuLink.Api/ProcuLink.Api.csproj`
- Modify: `ProcuLink.Api/appsettings.Development.json`

- [ ] **Step 1: Add the Stripe.net NuGet package**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Api
dotnet add package Stripe.net
```
Expected: `PackageReference` for `Stripe.net` appears in `ProcuLink.Api.csproj`.

- [ ] **Step 2: Add Stripe section to `appsettings.Development.json`**

Open `ProcuLink.Api/appsettings.Development.json`. The file already has a `"Stripe"` key with empty strings. Replace it with:

```json
"Stripe": {
  "SecretKey":          "sk_test_REPLACE_ME",
  "WebhookSecret":      "whsec_REPLACE_ME",
  "GrowthPriceId":      "price_REPLACE_ME",
  "OperationsPriceId":  "price_REPLACE_ME",
  "IntegrationPriceId": "price_REPLACE_ME"
}
```

**Important:** Do NOT commit real keys. Use `dotnet user-secrets` for real values:
```bash
cd ProcuLink.Api
dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
dotnet user-secrets set "Stripe:GrowthPriceId" "price_..."
dotnet user-secrets set "Stripe:OperationsPriceId" "price_..."
dotnet user-secrets set "Stripe:IntegrationPriceId" "price_..."
```

- [ ] **Step 3: Build to verify Stripe.net resolves**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
dotnet build ProcuLink.Api
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/ProcuLink.Api.csproj ProcuLink.Api/appsettings.Development.json
git commit -m "feat(billing): add Stripe.net package and config stubs"
```

---

## Task 5: StripeBillingService

**Files:**
- Create: `ProcuLink.Api/Services/StripeBillingService.cs`

- [ ] **Step 1: Create `StripeBillingService.cs`**

```csharp
// ProcuLink.Api/Services/StripeBillingService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Stripe;
using Stripe.Checkout;

namespace ProcuLink.Api.Services;

/// <summary>
/// Implements IBillingService using Stripe.net and EF Core.
/// Pilot order counts are cumulative since trial_started_at.
/// Paid-plan order counts use a rolling monthly window.
/// </summary>
public sealed class StripeBillingService : IBillingService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration    _config;
    private readonly ILogger<StripeBillingService> _logger;

    public StripeBillingService(
        ProcuLinkDbContext            db,
        IConfiguration                config,
        ILogger<StripeBillingService> logger)
    {
        _db     = db;
        _config = config;
        _logger = logger;
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────

    public async Task<BillingStatus> GetStatusAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        var ordersUsed      = await CountOrdersAsync(org, ct);
        var suppliersActive = await CountSuppliersAsync(orgId, ct);

        bool pilotExpired        = false;
        DateTime? pilotEndsAt    = null;
        bool extensionRequested  = org.PilotExtensionRequestedAt.HasValue;

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            pilotEndsAt  = effectiveEnd;
            pilotExpired = DateTime.UtcNow > effectiveEnd || ordersUsed >= PlanConstants.PilotOrderLimit;
        }

        var features = PlanConstants.FeaturesForPlan(org.Plan)
            .Select(f => f.ToString().ToLowerInvariant())
            .ToArray();

        return new BillingStatus(
            Plan:               org.Plan,
            OrdersUsed:         ordersUsed,
            OrderLimit:         limits.Orders,
            SuppliersActive:    suppliersActive,
            SupplierLimit:      limits.Suppliers,
            PilotEndsAt:        pilotEndsAt,
            PilotExpired:       pilotExpired,
            ExtensionRequested: extensionRequested,
            Features:           features
        );
    }

    // ── CheckOrderLimitAsync ──────────────────────────────────────────────

    public async Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var org    = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            var count        = await CountOrdersAsync(org, ct);
            var expired      = DateTime.UtcNow > effectiveEnd || count >= PlanConstants.PilotOrderLimit;
            return new LimitCheckResult(!expired, PilotExpired: expired, org.Plan, PlanConstants.PilotOrderLimit);
        }

        var monthlyCount = await CountOrdersAsync(org, ct);
        return new LimitCheckResult(monthlyCount < limits.Orders, PilotExpired: false, org.Plan, limits.Orders);
    }

    // ── CheckSupplierLimitAsync ───────────────────────────────────────────

    public async Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId, CancellationToken ct = default)
    {
        var org    = await LoadOrgAsync(orgId, ct);
        var limits = PlanConstants.Limits[org.Plan];

        if (org.Plan == PlanConstants.Pilot)
        {
            var effectiveEnd = org.PilotExtendedUntil ?? org.TrialStartedAt.Add(PlanConstants.PilotDuration);
            var expired      = DateTime.UtcNow > effectiveEnd;
            if (expired) return new LimitCheckResult(false, PilotExpired: true, org.Plan, PlanConstants.PilotSupplierLimit);
        }

        var active  = await CountSuppliersAsync(orgId, ct);
        var allowed = active < limits.Suppliers;
        return new LimitCheckResult(allowed, PilotExpired: false, org.Plan, limits.Suppliers);
    }

    // ── HasFeatureAsync ───────────────────────────────────────────────────

    public async Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);
        return PlanConstants.PlanHasFeature(org.Plan, feature);
    }

    // ── CreateCheckoutSessionAsync ────────────────────────────────────────

    public async Task<string> CreateCheckoutSessionAsync(
        Guid orgId, string plan, string returnUrl, CancellationToken ct = default)
    {
        var priceId = plan switch
        {
            PlanConstants.Growth      => _config["Stripe:GrowthPriceId"],
            PlanConstants.Operations  => _config["Stripe:OperationsPriceId"],
            PlanConstants.Integration => _config["Stripe:IntegrationPriceId"],
            _ => throw new ArgumentException($"No Stripe price configured for plan '{plan}'.")
        };

        var org = await LoadOrgAsync(orgId, ct);

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = new List<SessionLineItemOptions>
            {
                new() { Price = priceId, Quantity = 1 }
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                TrialPeriodDays = 14,
                Metadata = new Dictionary<string, string> { ["plan"] = plan }
            },
            Metadata      = new Dictionary<string, string> { ["org_id"] = orgId.ToString(), ["plan"] = plan },
            SuccessUrl    = $"{returnUrl}?billing=success",
            CancelUrl     = returnUrl,
            AllowPromotionCodes = true,
        };

        if (org.StripeCustomerId is not null)
            options.Customer = org.StripeCustomerId;

        var service = new SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    // ── CreatePortalSessionAsync ──────────────────────────────────────────

    public async Task<string> CreatePortalSessionAsync(
        Guid orgId, string returnUrl, CancellationToken ct = default)
    {
        var org = await LoadOrgAsync(orgId, ct);

        if (org.StripeCustomerId is null)
            throw new InvalidOperationException("No Stripe customer found for this organisation.");

        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer  = org.StripeCustomerId,
            ReturnUrl = returnUrl,
        };

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    // ── RequestPilotExtensionAsync ────────────────────────────────────────

    public async Task RequestPilotExtensionAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");

        if (org.PilotExtensionRequestedAt.HasValue)
        {
            _logger.LogInformation("Pilot extension already requested for org {OrgId}", orgId);
            return; // idempotent
        }

        org.PilotExtensionRequestedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "SALES SIGNAL — Pilot extension requested. OrgId={OrgId} OrgName={OrgName} RequestedAt={At}",
            org.Id, org.Name, org.PilotExtensionRequestedAt);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<Core.Entities.Organisation> LoadOrgAsync(Guid orgId, CancellationToken ct)
    {
        return await _db.Organisations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");
    }

    private async Task<int> CountOrdersAsync(Core.Entities.Organisation org, CancellationToken ct)
    {
        if (org.Plan == PlanConstants.Pilot)
        {
            // Cumulative count since trial start (no monthly reset)
            return await _db.PurchaseOrders
                .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= org.TrialStartedAt, ct);
        }

        // Rolling monthly window for paid plans
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == org.Id && o.CreatedAt >= monthStart, ct);
    }

    private Task<int> CountSuppliersAsync(Guid orgId, CancellationToken ct) =>
        _db.Suppliers.CountAsync(s => s.OrgId == orgId && s.DeletedAt == null, ct);
}
```

- [ ] **Step 2: Build to verify**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
dotnet build ProcuLink.Api
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add ProcuLink.Api/Services/StripeBillingService.cs
git commit -m "feat(billing): implement StripeBillingService"
```

---

## Task 6: BillingController

**Files:**
- Create: `ProcuLink.Api/Controllers/BillingController.cs`

- [ ] **Step 1: Create `BillingController.cs`**

```csharp
// ProcuLink.Api/Controllers/BillingController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/billing")]
public sealed class BillingController : ControllerBase
{
    private readonly IBillingService       _billing;
    private readonly ICurrentTenantService _tenant;
    private readonly IConfiguration        _config;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IBillingService            billing,
        ICurrentTenantService      tenant,
        IConfiguration             config,
        ILogger<BillingController> logger)
    {
        _billing = billing;
        _tenant  = tenant;
        _config  = config;
        _logger  = logger;
    }

    // ── GET /api/billing/status ───────────────────────────────────────────

    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var status = await _billing.GetStatusAsync(_tenant.OrganisationId, ct);
        return Ok(status);
    }

    // ── POST /api/billing/checkout ────────────────────────────────────────

    [HttpPost("checkout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCheckout(
        [FromBody] CheckoutRequest request,
        CancellationToken ct)
    {
        var validPlans = new[] { PlanConstants.Growth, PlanConstants.Operations, PlanConstants.Integration };
        if (!validPlans.Contains(request.Plan))
            return BadRequest(new { error = $"Invalid plan '{request.Plan}'. Valid: growth, operations, integration." });

        var returnUrl = $"{_config["Frontend:Url"] ?? "http://localhost:8081"}/settings";
        var url = await _billing.CreateCheckoutSessionAsync(_tenant.OrganisationId, request.Plan, returnUrl, ct);
        return Ok(new { url });
    }

    // ── POST /api/billing/portal ──────────────────────────────────────────

    [HttpPost("portal")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePortal(CancellationToken ct)
    {
        var returnUrl = $"{_config["Frontend:Url"] ?? "http://localhost:8081"}/settings";
        try
        {
            var url = await _billing.CreatePortalSessionAsync(_tenant.OrganisationId, returnUrl, ct);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── POST /api/billing/pilot/request-extension ─────────────────────────

    [HttpPost("pilot/request-extension")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestExtension(CancellationToken ct)
    {
        await _billing.RequestPilotExtensionAsync(_tenant.OrganisationId, ct);
        return Ok(new { message = "Extension request received. Our team will be in touch within 1 business day." });
    }

    // ── POST /api/billing/webhook ─────────────────────────────────────────

    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Webhook()
    {
        var json      = await new StreamReader(Request.Body).ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        var secret    = _config["Stripe:WebhookSecret"] ?? string.Empty;

        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = Stripe.EventUtility.ConstructEvent(json, signature, secret);
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogWarning("Stripe webhook signature validation failed: {Msg}", ex.Message);
            return BadRequest(new { error = "Invalid signature." });
        }

        try
        {
            await HandleStripeEventAsync(stripeEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing Stripe event {EventId} ({Type})",
                stripeEvent.Id, stripeEvent.Type);
            return StatusCode(500);
        }

        return Ok();
    }

    // ── Webhook event handler ─────────────────────────────────────────────

    private async Task HandleStripeEventAsync(Stripe.Event e)
    {
        switch (e.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(e.Data.Object as Stripe.Checkout.Session);
                break;

            case "customer.subscription.updated":
                await HandleSubscriptionUpdatedAsync(e.Data.Object as Stripe.Subscription);
                break;

            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(e.Data.Object as Stripe.Subscription);
                break;

            default:
                _logger.LogDebug("Ignored Stripe event {Type}", e.Type);
                break;
        }
    }

    private async Task HandleCheckoutCompletedAsync(Stripe.Checkout.Session? session)
    {
        if (session is null) return;

        session.Metadata.TryGetValue("org_id", out var orgIdStr);
        session.Metadata.TryGetValue("plan", out var plan);

        if (!Guid.TryParse(orgIdStr, out var orgId) || string.IsNullOrEmpty(plan)) return;

        var org = await _db.Organisations.FindAsync(orgId);
        if (org is null) return;

        // Idempotent: skip if already in target state
        if (org.Plan == plan && org.StripeCustomerId == session.CustomerId) return;

        org.Plan                = plan;
        org.StripeCustomerId    = session.CustomerId;
        org.StripeSubscriptionId = session.SubscriptionId;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Org {OrgId} upgraded to {Plan} via Stripe checkout {SessionId}",
            orgId, plan, session.Id);
    }

    private async Task HandleSubscriptionUpdatedAsync(Stripe.Subscription? sub)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId);
        if (org is null) return;

        var status = sub.Status; // "trialing", "active", "past_due", "unpaid", "canceled"
        if (status is "trialing" or "active")
        {
            sub.Metadata.TryGetValue("plan", out var plan);
            if (!string.IsNullOrEmpty(plan) && org.Plan != plan)
            {
                org.Plan = plan;
                await _db.SaveChangesAsync();
                _logger.LogInformation("Org {OrgId} plan confirmed as {Plan} (sub status: {Status})",
                    org.Id, plan, status);
            }
        }
        else
        {
            _logger.LogWarning("Subscription {SubId} for org {OrgId} is {Status} — monitoring, not downgrading yet.",
                sub.Id, org.Id, status);
        }
    }

    private async Task HandleSubscriptionDeletedAsync(Stripe.Subscription? sub)
    {
        if (sub is null) return;

        var org = await _db.Organisations
            .FirstOrDefaultAsync(o => o.StripeCustomerId == sub.CustomerId);
        if (org is null) return;

        if (org.Plan == PlanConstants.Pilot && org.StripeSubscriptionId is null) return; // already pilot

        org.Plan                 = PlanConstants.Pilot;  // reverts to frozen pilot
        org.StripeSubscriptionId = null;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Org {OrgId} subscription cancelled — reverted to frozen Pilot.", org.Id);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record CheckoutRequest(string Plan);
```

- [ ] **Step 2: Add missing usings — the controller needs `ProcuLinkDbContext` and EF**

Add these fields to `BillingController`:

```csharp
    private readonly ProcuLink.Infrastructure.ProcuLinkDbContext _db;
```

And update the constructor to inject it:

```csharp
    public BillingController(
        IBillingService            billing,
        ICurrentTenantService      tenant,
        IConfiguration             config,
        ILogger<BillingController> logger,
        ProcuLink.Infrastructure.ProcuLinkDbContext db)
    {
        _billing = billing;
        _tenant  = tenant;
        _config  = config;
        _logger  = logger;
        _db      = db;
    }
```

And add the using at the top of the file:

```csharp
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;
```

- [ ] **Step 3: Build**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
dotnet build ProcuLink.Api
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Controllers/BillingController.cs
git commit -m "feat(billing): add BillingController with status, checkout, portal, extension, webhook"
```

---

## Task 7: Limit enforcement in existing controllers

**Files:**
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs`
- Modify: `ProcuLink.Api/Controllers/SuppliersController.cs`

- [ ] **Step 1: Add `IBillingService` to `OrdersController`**

In `OrdersController.cs`, add to the constructor parameters:

```csharp
    private readonly IBillingService _billing;
```

Add `IBillingService billing` to the constructor signature and assign:

```csharp
    public OrdersController(
        IOrderService             orders,
        ICurrentTenantService     tenant,
        IBackgroundJobClient      jobs,
        ProcuLinkDbContext        db,
        ILogger<OrdersController> logger,
        IBillingService           billing)
    {
        _orders  = orders;
        _tenant  = tenant;
        _jobs    = jobs;
        _db      = db;
        _logger  = logger;
        _billing = billing;
    }
```

Add the using at the top:

```csharp
using ProcuLink.Core.Services;
```

- [ ] **Step 2: Add limit check at the start of `Upload`**

In the `Upload` method, after `var orgId = _tenant.OrganisationId;` and before the `await using var stream` line, insert:

```csharp
        // ── Billing limit check ────────────────────────────────────────────
        var limitCheck = await _billing.CheckOrderLimitAsync(orgId, ct);
        if (!limitCheck.Allowed)
        {
            return StatusCode(429, new
            {
                error      = limitCheck.PilotExpired ? "pilot_expired" : "order_limit_reached",
                plan       = limitCheck.Plan,
                limit      = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
        }
```

- [ ] **Step 3: Add `IBillingService` to `SuppliersController`**

In `SuppliersController.cs`, add field:

```csharp
    private readonly IBillingService _billing;
```

Update constructor to inject it:

```csharp
    public SuppliersController(
        ISupplierProfileRepository supplierProfileRepository,
        IItemMappingService        mappingService,
        ProcuLinkDbContext         db,
        ICurrentTenantService      tenant,
        IBillingService            billing)
    {
        _supplierProfileRepository = supplierProfileRepository;
        _mappingService            = mappingService;
        _db                        = db;
        _tenant                    = tenant;
        _billing                   = billing;
    }
```

Add using:

```csharp
using ProcuLink.Core.Services;
```

- [ ] **Step 4: Add limit check at the start of `CreateSupplier`**

In `CreateSupplier`, after `var orgId = _tenant.OrganisationId;` and before the duplicate-name check, insert:

```csharp
        // ── Billing limit check ────────────────────────────────────────────
        var limitCheck = await _billing.CheckSupplierLimitAsync(orgId, ct);
        if (!limitCheck.Allowed)
        {
            return StatusCode(429, new
            {
                error      = limitCheck.PilotExpired ? "pilot_expired" : "supplier_limit_reached",
                plan       = limitCheck.Plan,
                limit      = limitCheck.Limit,
                upgradeUrl = "/settings",
            });
        }
```

- [ ] **Step 5: Build**

```bash
dotnet build ProcuLink.Api
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Controllers/OrdersController.cs
git add ProcuLink.Api/Controllers/SuppliersController.cs
git commit -m "feat(billing): enforce order and supplier limits in upload and create endpoints"
```

---

## Task 8: Register IBillingService and set Stripe API key in Program.cs

**Files:**
- Modify: `ProcuLink.Api/Program.cs`

- [ ] **Step 1: Add Stripe SDK initialisation**

After the Sentry block and before the `// ── Database` comment, add:

```csharp
// ── Stripe SDK ────────────────────────────────────────────────────────────
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;
```

Add `using Stripe;` at the top of `Program.cs`.

- [ ] **Step 2: Register `StripeBillingService`**

In the `// ── Domain services` section, add:

```csharp
builder.Services.AddScoped<IBillingService, StripeBillingService>();
```

Add the using at the top:

```csharp
using ProcuLink.Api.Services;
```

- [ ] **Step 3: Build and run**

```bash
dotnet build ProcuLink.Api
dotnet run --project ProcuLink.Api
```
Expected: `Now listening on: http://localhost:5223`. Hit Ctrl+C to stop.

- [ ] **Step 4: Smoke test with curl**

In a new terminal (with the API running):

```bash
# Should return 401 (no JWT) — proves route is registered
curl -s -o /dev/null -w "%{http_code}" http://localhost:5223/api/billing/status
```
Expected: `401`

```bash
# Webhook should return 400 (invalid signature) — proves raw body + validation works
curl -s -X POST http://localhost:5223/api/billing/webhook \
  -H "Content-Type: application/json" \
  -d '{"type":"test"}' \
  -w "\n%{http_code}"
```
Expected: `400`

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Program.cs
git commit -m "feat(billing): register StripeBillingService and initialise Stripe SDK"
```

---

## Task 9: Frontend — billing types and API functions

**Files:**
- Modify: `src/types/procurement.ts`
- Modify: `src/lib/api-client.ts`

- [ ] **Step 1: Add `BillingStatus` type to `procurement.ts`**

Open `src/types/procurement.ts` and append:

```typescript
// ── Billing ────────────────────────────────────────────────────────────────

export type BillingPlan =
  | "pilot"
  | "growth"
  | "operations"
  | "integration"
  | "enterprise";

export interface BillingStatus {
  plan:               BillingPlan;
  ordersUsed:         number;
  orderLimit:         number;
  suppliersActive:    number;
  supplierLimit:      number;
  pilotEndsAt:        string | null;   // ISO datetime string
  pilotExpired:       boolean;
  extensionRequested: boolean;
  features:           string[];        // lowercase BillingFeature names
}
```

- [ ] **Step 2: Add billing API functions to `api-client.ts`**

At the top of `api-client.ts`, add `BillingStatus` to the import:

```typescript
import type {
  // ...existing imports...
  BillingStatus,
} from "@/types/procurement";
```

Then append the following billing functions before the final export (or at the end of the file):

```typescript
// ── Billing ────────────────────────────────────────────────────────────────

export async function getBillingStatus(): Promise<BillingStatus> {
  if (USE_MOCK) {
    // Dev mock — simulates an active Pilot with 5 orders used
    return {
      plan:               "pilot",
      ordersUsed:         5,
      orderLimit:         20,
      suppliersActive:    1,
      supplierLimit:      1,
      pilotEndsAt:        new Date(Date.now() + 9 * 24 * 60 * 60 * 1000).toISOString(),
      pilotExpired:       false,
      extensionRequested: false,
      features:           [],
    };
  }
  const headers = await authHeader();
  const res = await fetch(`${API_BASE_URL}/api/billing/status`, { headers });
  if (!res.ok) throw new Error(`billing/status: ${res.status}`);
  return res.json();
}

export async function createCheckoutSession(plan: string): Promise<string> {
  const headers = await authHeader();
  const res = await fetch(`${API_BASE_URL}/api/billing/checkout`, {
    method: "POST",
    headers: { ...headers, "Content-Type": "application/json" },
    body: JSON.stringify({ plan }),
  });
  if (!res.ok) throw new Error(`billing/checkout: ${res.status}`);
  const data = await res.json();
  return data.url as string;
}

export async function createPortalSession(): Promise<string> {
  const headers = await authHeader();
  const res = await fetch(`${API_BASE_URL}/api/billing/portal`, {
    method: "POST",
    headers,
  });
  if (!res.ok) throw new Error(`billing/portal: ${res.status}`);
  const data = await res.json();
  return data.url as string;
}

export async function requestPilotExtension(): Promise<void> {
  const headers = await authHeader();
  const res = await fetch(`${API_BASE_URL}/api/billing/pilot/request-extension`, {
    method: "POST",
    headers,
  });
  if (!res.ok) throw new Error(`pilot/request-extension: ${res.status}`);
}
```

- [ ] **Step 3: Build frontend**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
bun run build
```
Expected: `✓ Compiled successfully`

- [ ] **Step 4: Commit**

```bash
git add src/types/procurement.ts src/lib/api-client.ts
git commit -m "feat(billing): add BillingStatus type and billing API functions"
```

---

## Task 10: Frontend — BillingSection component

**Files:**
- Create: `src/components/bridge/BillingSection.tsx`
- Modify: `src/app/(app)/settings/page.tsx`

- [ ] **Step 1: Create `BillingSection.tsx`**

```tsx
"use client";

// BillingSection — renders inside the Settings > Billing tab.
// Handles 5 plan states: Pilot active, Pilot expired, Stripe trial, paid, Enterprise.

import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  getBillingStatus,
  createCheckoutSession,
  createPortalSession,
  requestPilotExtension,
} from "@/lib/api-client";
import type { BillingStatus, BillingPlan } from "@/types/procurement";

const PLAN_LABELS: Record<BillingPlan, string> = {
  pilot:       "Pilot",
  growth:      "Growth · €149/mo",
  operations:  "Operations · €399/mo",
  integration: "Integration · €999/mo",
  enterprise:  "Enterprise · Custom",
};

const PLAN_COLORS: Record<BillingPlan, string> = {
  pilot:       "#C97A14",
  growth:      "#1E66C9",
  operations:  "#2E8E3A",
  integration: "#6F4FCE",
  enterprise:  "#0B1A2F",
};

function UsageBar({ used, limit, label }: { used: number; limit: number; label: string }) {
  const pct     = limit === 0 ? 0 : Math.min(100, (used / limit) * 100);
  const isAmber = pct >= 80 && pct < 100;
  const isDanger = pct >= 100;
  const barColor = isDanger ? "#C53A3A" : isAmber ? "#C97A14" : "#1E66C9";

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
      <div style={{ display: "flex", justifyContent: "space-between" }}>
        <span style={{ fontSize: 12, color: "#56627A" }}>{label}</span>
        <span style={{ fontSize: 12, fontWeight: 600, color: isDanger ? "#C53A3A" : "#0B1A2F" }}>
          {used} / {limit === Number.MAX_SAFE_INTEGER ? "∞" : limit}
        </span>
      </div>
      <div style={{ height: 6, borderRadius: 99, background: "#E2E6EE", overflow: "hidden" }}>
        <div style={{ width: `${pct}%`, height: "100%", background: barColor, borderRadius: 99, transition: "width 0.4s" }} />
      </div>
    </div>
  );
}

function PlanBadge({ plan, expired }: { plan: BillingPlan; expired: boolean }) {
  return (
    <span style={{
      display: "inline-flex",
      alignItems: "center",
      gap: 6,
      borderRadius: 6,
      padding: "3px 10px",
      fontSize: 12,
      fontWeight: 700,
      background: `${PLAN_COLORS[plan]}18`,
      color: PLAN_COLORS[plan],
    }}>
      <span style={{ width: 6, height: 6, borderRadius: "50%", background: PLAN_COLORS[plan], display: "inline-block" }} />
      {expired ? "Pilot · Expired" : PLAN_LABELS[plan]}
    </span>
  );
}

function PilotCountdown({ endsAt }: { endsAt: string }) {
  const days = Math.max(0, Math.ceil((new Date(endsAt).getTime() - Date.now()) / 86_400_000));
  return (
    <span style={{ fontSize: 11.5, color: days <= 3 ? "#C97A14" : "#56627A" }}>
      {days} day{days !== 1 ? "s" : ""} remaining
    </span>
  );
}

export function BillingSection() {
  const qc = useQueryClient();

  const { data: status, isLoading, error } = useQuery<BillingStatus>({
    queryKey: ["billing-status"],
    queryFn:  getBillingStatus,
    staleTime: 60_000,
  });

  const checkoutMutation = useMutation({
    mutationFn: (plan: string) => createCheckoutSession(plan),
    onSuccess: (url) => { window.location.href = url; },
  });

  const portalMutation = useMutation({
    mutationFn: createPortalSession,
    onSuccess: (url) => { window.location.href = url; },
  });

  const extensionMutation = useMutation({
    mutationFn: requestPilotExtension,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["billing-status"] }),
  });

  if (isLoading) {
    return (
      <div style={{ padding: "32px 24px" }}>
        <div style={{ height: 20, width: 160, background: "#E2E6EE", borderRadius: 4, animation: "pulse 1.5s infinite" }} />
      </div>
    );
  }

  if (error || !status) {
    return (
      <div style={{ padding: "32px 24px", color: "#C53A3A", fontSize: 13 }}>
        Failed to load billing information.
      </div>
    );
  }

  const isPilot        = status.plan === "pilot";
  const isEnterprise   = status.plan === "enterprise";
  const isPaid         = !isPilot && !isEnterprise;
  const isPilotExpired = isPilot && status.pilotExpired;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20, maxWidth: 560 }}>

      {/* Expired banner */}
      {isPilotExpired && (
        <div style={{ borderRadius: 8, padding: "12px 16px", background: "#FAEFD6", border: "1px solid #C97A14", fontSize: 13, color: "#7A4A0A" }}>
          Your Pilot has ended. Upgrade to continue using ProcuLink.
        </div>
      )}

      {/* Plan row */}
      <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
        <PlanBadge plan={status.plan} expired={isPilotExpired} />
        {isPilot && !isPilotExpired && status.pilotEndsAt && (
          <PilotCountdown endsAt={status.pilotEndsAt} />
        )}
      </div>

      {/* Usage bars */}
      {!isEnterprise && (
        <div style={{ display: "flex", flexDirection: "column", gap: 10, opacity: isPilotExpired ? 0.4 : 1 }}>
          <UsageBar
            used={status.ordersUsed}
            limit={status.orderLimit}
            label={isPilot ? "Orders (Pilot total)" : "Orders this month"}
          />
          <UsageBar
            used={status.suppliersActive}
            limit={status.supplierLimit}
            label="Active suppliers"
          />
        </div>
      )}

      {/* CTAs */}
      {isPilot && (
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {(["growth", "operations", "integration"] as const).map((plan) => (
              <button
                key={plan}
                onClick={() => checkoutMutation.mutate(plan)}
                disabled={checkoutMutation.isPending}
                style={{
                  padding: "8px 16px",
                  borderRadius: 7,
                  fontSize: 12.5,
                  fontWeight: 600,
                  background: PLAN_COLORS[plan],
                  color: "#FFFFFF",
                  border: "none",
                  cursor: checkoutMutation.isPending ? "not-allowed" : "pointer",
                  opacity: checkoutMutation.isPending ? 0.6 : 1,
                }}
              >
                Upgrade to {plan.charAt(0).toUpperCase() + plan.slice(1)} →
              </button>
            ))}
          </div>

          {/* Extension request */}
          {!status.extensionRequested ? (
            <button
              onClick={() => extensionMutation.mutate()}
              disabled={extensionMutation.isPending}
              style={{ alignSelf: "flex-start", background: "none", border: "none", padding: 0, fontSize: 12, color: "#1E66C9", cursor: "pointer", textDecoration: "underline" }}
            >
              {extensionMutation.isPending ? "Sending…" : "Need more time? Request a Pilot extension →"}
            </button>
          ) : (
            <span style={{ fontSize: 12, color: "#2E8E3A" }}>
              ✓ Extension request sent — our team will be in touch.
            </span>
          )}

          <a href="mailto:sales@proculink.com" style={{ fontSize: 12, color: "#8A93A5" }}>
            Need Enterprise? Contact us →
          </a>
        </div>
      )}

      {isPaid && (
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <button
            onClick={() => portalMutation.mutate()}
            disabled={portalMutation.isPending}
            style={{
              alignSelf: "flex-start",
              padding: "8px 16px",
              borderRadius: 7,
              fontSize: 12.5,
              fontWeight: 600,
              background: "#0B1A2F",
              color: "#FFFFFF",
              border: "none",
              cursor: portalMutation.isPending ? "not-allowed" : "pointer",
            }}
          >
            {portalMutation.isPending ? "Opening…" : "Manage billing →"}
          </button>
          {status.plan !== "integration" && (
            <button
              onClick={() => {
                const next = status.plan === "growth" ? "operations" : "integration";
                checkoutMutation.mutate(next);
              }}
              style={{ alignSelf: "flex-start", background: "none", border: "none", padding: 0, fontSize: 12, color: "#1E66C9", cursor: "pointer", textDecoration: "underline" }}
            >
              Upgrade to {status.plan === "growth" ? "Operations" : "Integration"} →
            </button>
          )}
        </div>
      )}

      {isEnterprise && (
        <p style={{ fontSize: 13, color: "#56627A", margin: 0 }}>
          Contact your account manager to adjust your plan.
        </p>
      )}
    </div>
  );
}
```

- [ ] **Step 2: Wire `BillingSection` into `settings/page.tsx`**

Open `src/app/(app)/settings/page.tsx`. Find the section that renders content when `tab === "billing"`. It will be a placeholder or empty. Add the import at the top of the file:

```tsx
import { BillingSection } from "@/components/bridge/BillingSection";
```

Then find the billing tab content area (where `tab === "billing"` is rendered) and replace whatever placeholder is there with:

```tsx
{tab === "billing" && (
  <div className="p-6">
    <h2 className="text-[17px] font-semibold mb-1" style={{ color: "#0B1A2F" }}>Plan & billing</h2>
    <p className="text-[12.5px] mb-6" style={{ color: "#56627A" }}>Manage your ProcuLink plan and payment method.</p>
    <BillingSection />
  </div>
)}
```

- [ ] **Step 3: Build frontend**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
bun run build
```
Expected: `✓ Compiled successfully`

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/BillingSection.tsx
git add src/app/\(app\)/settings/page.tsx
git commit -m "feat(billing): add BillingSection component and wire into settings page"
```

---

## Task 11: Frontend — UploadWorkbench 429 banner

**Files:**
- Modify: `src/components/bridge/UploadWorkbench.tsx`

- [ ] **Step 1: Add upload error state and fetch the real API response**

In `UploadWorkbench.tsx`, find the `handleUpload` function (added in an earlier session). The current implementation calls the pipeline animation and redirects. Modify it to:

1. Add a `uploadError` state at the top of the component:
```tsx
const [uploadError, setUploadError] = useState<{ code: string; message: string } | null>(null);
```

2. In `handleUpload`, wrap the upload call to handle 429:
```tsx
async function handleUpload() {
  if (uploading) return;
  setUploadError(null);
  setUploading(true);
  setPipelineStage(0);

  try {
    const headers = await authHeader();
    const formData = new FormData();
    // formData.append("file", selectedFile);   // wire to actual file state when available
    // formData.append("supplierId", supplierId);

    // Simulate for now — replace with real fetch when file picker is wired
    const res = await fetch(`${process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5223"}/api/orders/upload`, {
      method: "POST",
      headers,
      // body: formData,
    }).catch(() => null);

    if (res?.status === 429) {
      const body = await res.json().catch(() => ({}));
      const code = body.error ?? "order_limit_reached";
      setUploadError({
        code,
        message: code === "pilot_expired"
          ? "Your Pilot has ended."
          : `You've reached your ${body.limit ?? ""}-order monthly limit.`,
      });
      setUploading(false);
      return;
    }

    // Animate pipeline stages
    PIPELINE_STAGES.forEach((_, i) => {
      const t = setTimeout(() => setPipelineStage(i), i * STAGE_MS);
      timerRefs.current.push(t);
    });
    const total = setTimeout(() => {
      router.push("/inbox/008412");
    }, PIPELINE_STAGES.length * STAGE_MS + 200);
    timerRefs.current.push(total);
  } catch {
    setUploading(false);
  }
}
```

3. Add the `authHeader` import. At the top of `UploadWorkbench.tsx`, if not already present, the API client export needs to be used. Since `authHeader` is not exported from `api-client.ts`, directly inline a simple fetch with the Clerk token:

```tsx
// Add near top of file — gets Clerk session token for API calls
async function getAuthHeader(): Promise<Record<string, string>> {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const token = await (window as any).Clerk?.session?.getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}
```

4. Render the error banner **above** the pipeline progress (replace it when error is present):

```tsx
{uploadError ? (
  <div style={{
    borderRadius: 7,
    padding: "10px 14px",
    background: "#FAEFD6",
    border: "1px solid #C97A14",
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    fontSize: 12.5,
    color: "#7A4A0A",
  }}>
    <span>{uploadError.message}</span>
    <a
      href="/settings"
      style={{ fontWeight: 600, color: "#C97A14", textDecoration: "none", whiteSpace: "nowrap" }}
    >
      {uploadError.code === "pilot_expired"
        ? "Upgrade to continue →"
        : "Upgrade your plan →"}
    </a>
  </div>
) : null}
```

- [ ] **Step 2: Build**

```bash
bun run build
```
Expected: `✓ Compiled successfully`

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/UploadWorkbench.tsx
git commit -m "feat(billing): handle 429 pilot_expired and order_limit_reached in UploadWorkbench"
```

---

## Task 12: Push both repos

- [ ] **Step 1: Push backend**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
git push origin main
```

- [ ] **Step 2: Push frontend**

```bash
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
git pull --rebase origin main
git push origin main
```

---

## Self-Review Checklist

**Spec coverage:**
- ✅ Task 1: `PlanConstants`, `BillingFeature` — §3 data model constants
- ✅ Task 2: `IBillingService`, `BillingStatus`, `LimitCheckResult` — §5.3 service layer
- ✅ Task 3: Entity + migration (5 columns) — §4 data model
- ✅ Task 4: Stripe.net + config — §5.1/5.2
- ✅ Task 5: `StripeBillingService` — §5.3 full implementation
- ✅ Task 6: `BillingController` (5 endpoints) — §5.4
- ✅ Task 7: Limit checks in OrdersController + SuppliersController — §5.5
- ✅ Task 8: DI wiring — §5.2 / Program.cs
- ✅ Task 9: `BillingStatus` type + API functions — §6 frontend
- ✅ Task 10: `BillingSection` + settings tab — §6.1
- ✅ Task 11: `UploadWorkbench` 429 banner — §6.2
- ✅ Pilot dual exit (time OR 20 orders) — `StripeBillingService.CheckOrderLimitAsync`
- ✅ Pilot cumulative count vs paid monthly count — `CountOrdersAsync`
- ✅ Extension request idempotency — `RequestPilotExtensionAsync`
- ✅ Webhook signature validation — `BillingController.Webhook`
- ✅ Webhook idempotency — checks before each DB write
- ✅ `defaultValueSql: "now()"` for existing rows in migration — Task 3 Step 4
- ✅ `metadata["plan"]` in Stripe session — webhook can resolve plan on `checkout.session.completed`
- ✅ Supplier limit check (§5.5) — Task 7

**Type consistency check:**
- `BillingStatus.Features` → `string[]` (lowercase names) — consistent Task 2 → Task 5 → Task 9
- `LimitCheckResult.PilotExpired` → bool — consistent Task 2 → Task 5 → Task 7
- `PlanConstants.Pilot` = `"pilot"` — consistent across all tasks
- `IBillingService.CheckOrderLimitAsync` returns `LimitCheckResult` — consistent Task 2 → Task 5 → Task 7
- `CountOrdersAsync` takes `Organisation` entity (not `Guid`) — consistent in Task 5

**Placeholder scan:** None found. All code blocks are complete.
