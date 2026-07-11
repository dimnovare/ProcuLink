# Stripe → Plan Reconciliation Job Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an idempotent daily Hangfire job that re-derives every org's `plan` + `account_status` from Stripe as source of truth, correcting drift and downgrading vanished/dead subscriptions after a 3-day grace.

**Architecture:** A new `IBillingReconciliationService` (impl in `ProcuLink.Api`, where Stripe.net lives) fetches each org's subscription via `SubscriptionService.GetAsync` and writes corrected state, org-scoped. A thin `BillingReconciliationJob` in the Worker sweeps all orgs with a non-null `StripeSubscriptionId` and delegates per org (try/catch isolation). Plan/status mapping is extracted from `BillingController` into a shared `StripeBillingMapping` helper both the webhook and reconciliation use, so they cannot drift.

**Tech Stack:** .NET 8, EF Core 8 (Npgsql), Stripe.net 51.1.0, Hangfire, xUnit + FluentAssertions.

## Global Constraints

- **LIVE billing / real money.** Merge + deploy require the founder present (CLAUDE.md). Tests use a faked `IStripeClient` only — NEVER real Stripe, NEVER prod.
- **All EF queries org-scoped:** `.Where(x => x.OrganisationId/​Id == …)` — no exceptions.
- **Idempotent** Hangfire jobs (mandatory).
- **No raw SQL** — EF Core only.
- **Worker process never sets `StripeConfiguration.ApiKey`** — the service MUST build its own `StripeClient` from `Stripe:SecretKey` (never rely on the process-global), else Stripe 401s in the Worker.
- Downgrade decisions (locked): frozen **Pilot + `read_only`**; **3-day** confirmed-missing grace via new nullable column; clear **`StripeSubscriptionId` + `StripePriceId`**, keep `StripeCustomerId`, set status `canceled`; cadence **daily 02:00 UTC** (`0 2 * * *`).
- Work happens in the worktree `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\.claude\worktrees\practical-swartz-03868d` — NOT the main checkout.
- Spec: `docs/superpowers/specs/2026-07-10-stripe-billing-reconciliation-design.md`.

---

## File Structure

- **Create** `ProcuLink.Api/Services/StripeBillingMapping.cs` — shared static plan/status mapping.
- **Modify** `ProcuLink.Api/Controllers/BillingController.cs` — delegate to `StripeBillingMapping` (byte-identical behavior).
- **Create** `ProcuLink.Api.Tests/Services/StripeBillingMappingTests.cs` — direct unit tests of the helper.
- **Modify** `ProcuLink.Core/Entities/Organisation.cs` — add `StripeReconciliationMissingSince`.
- **Create** `ProcuLink.Infrastructure/Migrations/<ts>_AddStripeReconciliationMissingSince.cs` (+ Designer) via `dotnet ef`; snapshot auto-updated.
- **Create** `ProcuLink.Core/Services/IBillingReconciliationService.cs` — the interface.
- **Create** `ProcuLink.Api/Services/StripeSubscriptionReconciliationService.cs` — the reconciliation logic.
- **Create** `ProcuLink.Api.Tests/Services/StripeSubscriptionReconciliationServiceTests.cs` — the 13 cases (+ shared `FakeStripeClient`).
- **Create** `ProcuLink.Worker/Jobs/BillingReconciliationJob.cs` — the sweep.
- **Create** `ProcuLink.Api.Tests/Jobs/BillingReconciliationJobTests.cs` — sweep predicate + per-org isolation.
- **Modify** `ProcuLink.Worker/Worker.cs` — register the recurring job (`0 2 * * *`).
- **Modify** `ProcuLink.Worker/Program.cs` (+ `ProcuLink.Api/Program.cs` for parity) — DI registration.

---

### Task 1: Shared `StripeBillingMapping` helper + refactor `BillingController`

**Files:**
- Create: `ProcuLink.Api/Services/StripeBillingMapping.cs`
- Test: `ProcuLink.Api.Tests/Services/StripeBillingMappingTests.cs`
- Modify: `ProcuLink.Api/Controllers/BillingController.cs` (`MapPriceIdToPlan` ~:533; the `sub.Status switch` ~:330)

**Interfaces:**
- Produces: `StripeBillingMapping.MapPriceIdToPlan(IConfiguration config, string? priceId) : string?` and `StripeBillingMapping.MapStatusToAccountStatus(string? stripeStatus, string currentStatus) : string` (param is `string?` — `sub.Status` is nullable; the `_` arm handles null).

- [ ] **Step 1: Write the failing tests**

```csharp
// ProcuLink.Api.Tests/Services/StripeBillingMappingTests.cs
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class StripeBillingMappingTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:GrowthPriceId"]            = "price_growth_m",
            ["Stripe:GrowthYearlyPriceId"]      = "price_growth_y",
            ["Stripe:OperationsPriceId"]        = "price_ops_m",
            ["Stripe:IntegrationPriceId"]       = "price_int_m",
            ["Stripe:DistributorPriceId"]       = "price_dist_m",
        }).Build();

    [Theory]
    [InlineData("price_growth_m", PlanConstants.Growth)]
    [InlineData("price_growth_y", PlanConstants.Growth)]
    [InlineData("price_ops_m",    PlanConstants.Operations)]
    [InlineData("price_int_m",    PlanConstants.Integration)]
    [InlineData("price_dist_m",   PlanConstants.Distributor)]
    public void MapPriceIdToPlan_MapsKnownPrices(string priceId, string expected) =>
        StripeBillingMapping.MapPriceIdToPlan(Config(), priceId).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("price_unknown")]
    public void MapPriceIdToPlan_UnknownOrBlank_ReturnsNull(string? priceId) =>
        StripeBillingMapping.MapPriceIdToPlan(Config(), priceId).Should().BeNull();

    [Theory]
    [InlineData("trialing", AccountStatusConstants.Trialing)]
    [InlineData("active",   AccountStatusConstants.Active)]
    [InlineData("past_due", AccountStatusConstants.PastDue)]
    [InlineData("unpaid",   AccountStatusConstants.PastDue)]
    [InlineData("canceled", AccountStatusConstants.ReadOnly)]
    public void MapStatusToAccountStatus_MapsKnownStatuses(string stripeStatus, string expected) =>
        StripeBillingMapping.MapStatusToAccountStatus(stripeStatus, "current").Should().Be(expected);

    [Fact]
    public void MapStatusToAccountStatus_Unknown_KeepsCurrent() =>
        StripeBillingMapping.MapStatusToAccountStatus("incomplete", AccountStatusConstants.Active)
            .Should().Be(AccountStatusConstants.Active);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter FullyQualifiedName~StripeBillingMappingTests`
Expected: FAIL — `StripeBillingMapping` does not exist.

- [ ] **Step 3: Create the helper** (copy the mapping verbatim from `BillingController`)

```csharp
// ProcuLink.Api/Services/StripeBillingMapping.cs
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;

namespace ProcuLink.Api.Services;

/// <summary>
/// Single source of truth for Stripe → ProcuLink plan/status mapping, shared by the
/// billing webhook (<see cref="Controllers.BillingController"/>) and the reconciliation
/// service so the two can never drift. Pure functions — config in, constant out.
/// </summary>
public static class StripeBillingMapping
{
    /// <summary>priceId → plan constant (config-driven), or null when unrecognised (caller keeps existing plan).</summary>
    public static string? MapPriceIdToPlan(IConfiguration config, string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == config["Stripe:GrowthPriceId"]) return PlanConstants.Growth;
        if (priceId == config["Stripe:GrowthYearlyPriceId"]) return PlanConstants.Growth;
        if (priceId == config["Stripe:OperationsPriceId"]) return PlanConstants.Operations;
        if (priceId == config["Stripe:OperationsYearlyPriceId"]) return PlanConstants.Operations;
        if (priceId == config["Stripe:IntegrationPriceId"]) return PlanConstants.Integration;
        if (priceId == config["Stripe:IntegrationYearlyPriceId"]) return PlanConstants.Integration;
        if (priceId == config["Stripe:DistributorPriceId"]) return PlanConstants.Distributor;
        if (priceId == config["Stripe:DistributorYearlyPriceId"]) return PlanConstants.Distributor;
        return null;
    }

    /// <summary>
    /// Stripe subscription.status → AccountStatus. Mirrors the switch in
    /// <c>BillingController.HandleSubscriptionUpdatedAsync</c> exactly; unknown statuses
    /// keep the caller's current status.
    /// </summary>
    public static string MapStatusToAccountStatus(string? stripeStatus, string currentStatus) => stripeStatus switch
    {
        "trialing"            => AccountStatusConstants.Trialing,
        "active"              => AccountStatusConstants.Active,
        "past_due" or "unpaid"=> AccountStatusConstants.PastDue,
        "canceled"            => AccountStatusConstants.ReadOnly,
        _                     => currentStatus,
    };
}
```

- [ ] **Step 4: Refactor `BillingController` to delegate** (behavior byte-identical)

In `HandleSubscriptionUpdatedAsync` (~:321) replace `var mappedPlan = MapPriceIdToPlan(priceId);` with
`var mappedPlan = StripeBillingMapping.MapPriceIdToPlan(_config, priceId);`. Replace the `org.AccountStatus = sub.Status switch { … };` block (~:330–337) with:
`org.AccountStatus = StripeBillingMapping.MapStatusToAccountStatus(sub.Status, org.AccountStatus);`
In `HandleCheckoutCompletedAsync` (~:286) replace `var mappedPlan = MapPriceIdToPlan(priceId) ?? plan;` with
`var mappedPlan = StripeBillingMapping.MapPriceIdToPlan(_config, priceId) ?? plan;`
Delete the now-unused private `MapPriceIdToPlan` method (~:533–545). Leave `HandleCheckoutCompletedAsync`'s `trialing?Trialing:Active` line and `HandleSubscriptionDeletedAsync`'s hard `ReadOnly` set inline (not shared — see spec).

- [ ] **Step 5: Run the helper tests + the full BillingController suite**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~StripeBillingMappingTests|FullyQualifiedName~BillingControllerTests"`
Expected: PASS — helper tests green AND every `BillingControllerTests` assertion green (proves the extraction is byte-identical).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Services/StripeBillingMapping.cs ProcuLink.Api.Tests/Services/StripeBillingMappingTests.cs ProcuLink.Api/Controllers/BillingController.cs
git commit -m "refactor(billing): extract shared StripeBillingMapping (webhook byte-identical)"
```

---

### Task 2: Add `StripeReconciliationMissingSince` column + EF migration

**Files:**
- Modify: `ProcuLink.Core/Entities/Organisation.cs` (after the Stripe block, ~:44)
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` (Organisation block, after `BillingUpdatedAt` ~:250)
- Create: migration via `dotnet ef` (see Step 3) + auto-updated `ProcuLinkDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `Organisation.StripeReconciliationMissingSince : DateTime?`

- [ ] **Step 1: Add the property**

```csharp
// ProcuLink.Core/Entities/Organisation.cs — after StripeSubscriptionStatus / BillingUpdatedAt
/// <summary>
/// UTC instant the reconciliation job FIRST observed this org's Stripe subscription missing
/// (Stripe GetAsync 404 resource_missing). NULL = not currently observed missing. Drives the
/// 3-day grace window before a vanished subscription is downgraded to frozen Pilot + read_only,
/// protecting a real paying customer from a transient 404 / live-vs-test key slip. Cleared when
/// the subscription reappears healthy (self-heal) or once the downgrade is applied.
/// </summary>
public DateTime? StripeReconciliationMissingSince { get; set; }
```

- [ ] **Step 2: Add the EXPLICIT column mapping** (this repo maps every column by hand — there is NO global snake_case convention, so without this the column lands as PascalCase `StripeReconciliationMissingSince`)

```csharp
// ProcuLink.Infrastructure/ProcuLinkDbContext.cs — inside modelBuilder.Entity<Organisation>(b => {…}),
// right after the b.Property(x => x.BillingUpdatedAt)… mapping (~:250)
b.Property(x => x.StripeReconciliationMissingSince)
 .HasColumnName("stripe_reconciliation_missing_since")
 .HasColumnType("timestamptz");
```

- [ ] **Step 3: Generate the migration**

Run from the worktree root (confirmed canonical for this repo — no design-time factory; connection string comes from `ProcuLink.Api/appsettings.Development.json`):
```bash
dotnet ef migrations add AddStripeReconciliationMissingSince --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```
Expected: creates `ProcuLink.Infrastructure/Migrations/<timestamp>_AddStripeReconciliationMissingSince.cs` (+ `.Designer.cs`) and updates `ProcuLinkDbContextModelSnapshot.cs`. The `Up()` must be a single `AddColumn<DateTime>(name: "stripe_reconciliation_missing_since", table: "organisations", type: "timestamptz", nullable: true)` and `Down()` a `DropColumn`. The explicit mapping from Step 2 guarantees the snake_case column name + `timestamptz` type (compare to `stripe_subscription_status`).

- [ ] **Step 4: Build to verify the model compiles**

Run: `dotnet build ProcuLink.Infrastructure`
Expected: Build succeeded. (Do NOT run `database update` — no local/prod DB writes in this task.)

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Entities/Organisation.cs ProcuLink.Infrastructure/Services/../ProcuLinkDbContext.cs ProcuLink.Infrastructure/Migrations/
git commit -m "feat(billing): add organisations.stripe_reconciliation_missing_since (grace marker)"
```

---

### Task 3: `IBillingReconciliationService` + `StripeSubscriptionReconciliationService`

**Files:**
- Create: `ProcuLink.Core/Services/IBillingReconciliationService.cs`
- Create: `ProcuLink.Api/Services/StripeSubscriptionReconciliationService.cs`
- Test: `ProcuLink.Api.Tests/Services/StripeSubscriptionReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: `StripeBillingMapping` (Task 1); `Organisation.StripeReconciliationMissingSince` (Task 2); `IBillingService.EmitBillingCancelledAsync`.
- Produces: `IBillingReconciliationService.ReconcileOrgAsync(Guid orgId, CancellationToken ct = default) : Task`.

- [ ] **Step 1: Define the interface**

```csharp
// ProcuLink.Core/Services/IBillingReconciliationService.cs
namespace ProcuLink.Core.Services;

/// <summary>
/// Reconciles one organisation's persisted billing state (plan + account_status) against
/// Stripe as source of truth. Safety net for missed webhooks and stale/test-mode subscription
/// ids. Org-scoped and idempotent. No-op when Stripe is not configured.
/// </summary>
public interface IBillingReconciliationService
{
    Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing tests** (13 cases + reusable `FakeStripeClient`)

```csharp
// ProcuLink.Api.Tests/Services/StripeSubscriptionReconciliationServiceTests.cs
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Stripe;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class StripeSubscriptionReconciliationServiceTests
{
    // ── faked Stripe transport: returns a canned Subscription or throws ─────
    private sealed class FakeStripeClient : IStripeClient
    {
        private readonly object _response; // Subscription to return, or Exception to throw
        public int Calls { get; private set; }
        public FakeStripeClient(object response) => _response = response;

        public string ApiBase => "https://api.stripe.invalid";
        public string ApiKey => "sk_test_fake";
        public string ClientId => "ca_fake";
        public string ConnectBase => "https://connect.stripe.invalid";
        public string FilesBase => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter.stripe.invalid";

        public Task<T> RequestAsync<T>(HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken ct = default) where T : IStripeEntity
        {
            Calls++;
            if (_response is Exception ex) throw ex;
            return Task.FromResult((T)_response);
        }
        public Task<System.IO.Stream> RequestStreamingAsync(HttpMethod method, string path,
            BaseOptions options, RequestOptions requestOptions, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static Subscription Sub(string status, string? priceId) => new()
    {
        Id = "sub_123",
        Status = status,
        Items = new StripeList<SubscriptionItem>
        {
            Data = new List<SubscriptionItem> { new() { Price = priceId is null ? null : new Price { Id = priceId } } }
        }
    };

    // CONFIRM the StripeException ctor shape against Stripe.net 51.1.0 via recon Agent A.
    private static StripeException NotFound() =>
        new(HttpStatusCode.NotFound, new StripeError { Code = "resource_missing", Type = "invalid_request_error" },
            "No such subscription: sub_123");
    private static StripeException Unauthorized() =>
        new(HttpStatusCode.Unauthorized, new StripeError { Type = "invalid_request_error" }, "Invalid API Key provided");

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration Config(bool stripeConfigured = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"]         = stripeConfigured ? "sk_test_fake" : "",
            ["Stripe:GrowthPriceId"]     = "price_growth_m",
            ["Stripe:OperationsPriceId"] = "price_ops_m",
        }).Build();

    private static StripeSubscriptionReconciliationService MakeSvc(
        ProcuLinkDbContext db, IConfiguration config, IStripeClient? stripe, out FakeAnalyticsService analytics)
    {
        analytics = new FakeAnalyticsService();
        var billing = new StripeBillingService(db, config, NullLogger<StripeBillingService>.Instance, analytics);
        return new StripeSubscriptionReconciliationService(
            db, config, billing, NullLogger<StripeSubscriptionReconciliationService>.Instance, stripe);
    }

    private static async Task<Organisation> AddOrgAsync(ProcuLinkDbContext db, string plan, string status,
        string? subId = "sub_123", string? priceId = "price_growth_m", DateTime? missingSince = null)
    {
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id = id, ClerkOrgId = $"org_{id:N}", Name = "Recon Org", Slug = $"recon-{id:N}",
            Plan = plan, AccountStatus = status, StripeCustomerId = "cus_keep",
            StripeSubscriptionId = subId, StripePriceId = priceId,
            StripeReconciliationMissingSince = missingSince,
            CreatedAt = DateTime.UtcNow.AddDays(-30), TrialStartedAt = DateTime.UtcNow.AddDays(-30),
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    // 1. drifted plan on a healthy active sub → corrected
    [Fact]
    public async Task HealthyActive_DriftedPlan_CorrectedToStripe()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Operations, AccountStatusConstants.Active, priceId: "price_growth_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
    }

    // 2. missed upgrade: pilot in DB, active growth in Stripe → upgraded
    [Fact]
    public async Task MissedUpgrade_PilotToActiveGrowth_Upgraded()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Pilot, AccountStatusConstants.Trialing);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
    }

    // 3. past_due → account_status past_due, plan kept
    [Fact]
    public async Task PastDue_KeepsPlan_SetsPastDue()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("past_due", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.PastDue);
    }

    // 4. canceled status (resolves) → immediate downgrade, ids cleared per (c)
    [Fact]
    public async Task CanceledStatus_ImmediateDowngrade_ClearsSubAndPriceKeepsCustomer()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("canceled", "price_growth_m")), out var analytics);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        after.StripeSubscriptionId.Should().BeNull();
        after.StripePriceId.Should().BeNull();
        after.StripeSubscriptionStatus.Should().Be("canceled");
        after.StripeCustomerId.Should().Be("cus_keep");
        after.StripeReconciliationMissingSince.Should().BeNull();
        analytics.Captured.Should().Contain(e => e.EventName == "billing_cancelled");
    }

    // 5. 404 first run → MissingSince set, NO plan change
    [Fact]
    public async Task Missing_FirstRun_SetsMarker_NoDowngrade()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
        after.StripeReconciliationMissingSince.Should().NotBeNull();
        after.StripeSubscriptionId.Should().Be("sub_123");
    }

    // 6. 404 with MissingSince 4 days ago → downgrade
    [Fact]
    public async Task Missing_PastGrace_Downgrades()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-4));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        after.StripeSubscriptionId.Should().BeNull();
    }

    // 7. 404 within grace (1 day) → no change
    [Fact]
    public async Task Missing_WithinGrace_NoChange()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-1));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.StripeSubscriptionId.Should().Be("sub_123");
    }

    // 8. self-heal: MissingSince set, Stripe healthy again → cleared
    [Fact]
    public async Task Healthy_ClearsStaleMissingMarker()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-1));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.StripeReconciliationMissingSince.Should().BeNull();
        after.Plan.Should().Be(PlanConstants.Growth);
    }

    // 9. SAFETY: 401 auth error → never downgrades, state untouched
    [Fact]
    public async Task AuthError_NeverDowngrades_StateUntouched()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Unauthorized()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
        after.StripeSubscriptionId.Should().Be("sub_123");
        after.StripeReconciliationMissingSince.Should().BeNull();
    }

    // 10. org-scope: reconciling one org leaves the other untouched
    [Fact]
    public async Task OrgScoped_OtherOrgUntouched()
    {
        var db = MakeDb();
        var target = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var other  = await AddOrgAsync(db, PlanConstants.Operations, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("canceled", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(target.Id);
        var otherAfter = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == other.Id);
        otherAfter.Plan.Should().Be(PlanConstants.Operations);
        otherAfter.AccountStatus.Should().Be(AccountStatusConstants.Active);
        otherAfter.StripeSubscriptionId.Should().Be("sub_123");
    }

    // 11. Stripe not configured → no-op
    [Fact]
    public async Task StripeNotConfigured_NoOp()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var fake = new FakeStripeClient(NotFound());
        var svc = MakeSvc(db, Config(stripeConfigured: false), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        fake.Calls.Should().Be(0, "the sweep must never call Stripe without a configured key");
    }

    // 12. null subscription id → skipped, Stripe never called
    [Fact]
    public async Task NullSubscriptionId_Skipped()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, subId: null, priceId: null);
        var fake = new FakeStripeClient(NotFound());
        var svc = MakeSvc(db, Config(), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        fake.Calls.Should().Be(0);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter FullyQualifiedName~StripeSubscriptionReconciliationServiceTests`
Expected: FAIL — service type does not exist.

- [ ] **Step 4: Implement the service**

```csharp
// ProcuLink.Api/Services/StripeSubscriptionReconciliationService.cs
using System.Net;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Stripe;

namespace ProcuLink.Api.Services;

/// <summary>
/// Re-derives one org's plan + account_status from Stripe as source of truth. Idempotent,
/// org-scoped. No-op without a configured Stripe key. Builds its OWN StripeClient from config
/// so it works in the Worker process (which never sets StripeConfiguration.ApiKey).
/// </summary>
public sealed class StripeSubscriptionReconciliationService : IBillingReconciliationService
{
    /// <summary>Confirmed-missing grace before a vanished (404) subscription is downgraded.</summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromDays(3);

    private readonly ProcuLinkDbContext _db;
    private readonly IConfiguration _config;
    private readonly IBillingService _billing;   // EmitBillingCancelledAsync on downgrade
    private readonly ILogger<StripeSubscriptionReconciliationService> _logger;
    private readonly IStripeClient? _stripeClient; // test seam; null in prod → built from config

    public StripeSubscriptionReconciliationService(
        ProcuLinkDbContext db,
        IConfiguration config,
        IBillingService billing,
        ILogger<StripeSubscriptionReconciliationService> logger,
        IStripeClient? stripeClient = null)
    {
        _db = db; _config = config; _billing = billing; _logger = logger; _stripeClient = stripeClient;
    }

    public async Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default)
    {
        var secret = _config["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret)) return; // never touch state without a configured key

        var org = await _db.Organisations.FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null || string.IsNullOrWhiteSpace(org.StripeSubscriptionId)) return;

        var client = _stripeClient ?? new StripeClient(secret);
        var subscriptions = new SubscriptionService(client);

        Subscription sub;
        try
        {
            sub = await subscriptions.GetAsync(org.StripeSubscriptionId, cancellationToken: ct);
        }
        catch (StripeException ex) when (IsResourceMissing(ex))
        {
            await HandleMissingAsync(org, ct);
            return;
        }
        catch (StripeException ex)
        {
            // 401 auth / 429 / 5xx / network — transient or config; NEVER downgrade on these.
            _logger.LogError(ex,
                "Reconcile: non-404 Stripe error for org {OrgId} sub {SubId} (status {Http}); leaving state untouched.",
                org.Id, org.StripeSubscriptionId, ex.HttpStatusCode);
            return;
        }

        await ApplyResolvedAsync(org, sub, ct);
    }

    private static bool IsResourceMissing(StripeException ex) =>
        ex.HttpStatusCode == HttpStatusCode.NotFound
        && string.Equals(ex.StripeError?.Code, "resource_missing", StringComparison.Ordinal);

    private async Task ApplyResolvedAsync(Organisation org, Subscription sub, CancellationToken ct)
    {
        var status = sub.Status ?? string.Empty;
        var priceId = sub.Items?.Data?.FirstOrDefault()?.Price?.Id;

        // DEAD (resolves but authoritative) → immediate downgrade, no grace.
        if (status is "canceled" or "incomplete_expired")
        {
            await DowngradeAsync(org, org.StripeSubscriptionId!, ct);
            return;
        }

        var targetPlan = org.Plan;
        var targetStatus = org.AccountStatus;

        if (status is "active" or "trialing")
        {
            var mapped = StripeBillingMapping.MapPriceIdToPlan(_config, priceId);
            if (!string.IsNullOrEmpty(mapped)) targetPlan = mapped; // keep existing when unrecognised (manual Enterprise)
            targetStatus = StripeBillingMapping.MapStatusToAccountStatus(status, org.AccountStatus);
        }
        else if (status is "past_due" or "unpaid")
        {
            targetStatus = StripeBillingMapping.MapStatusToAccountStatus(status, org.AccountStatus); // plan kept
        }
        else
        {
            _logger.LogInformation("Reconcile: org {OrgId} sub status '{Status}' unmapped — plan/status unchanged.", org.Id, status);
        }

        var changed = false;
        if (org.Plan != targetPlan) { org.Plan = targetPlan; changed = true; }
        if (org.AccountStatus != targetStatus) { org.AccountStatus = targetStatus; changed = true; }
        // Only overwrite the price id with a real value — never null out a live sub's price.
        if (!string.IsNullOrEmpty(priceId) && org.StripePriceId != priceId) { org.StripePriceId = priceId; changed = true; }
        if (org.StripeSubscriptionStatus != status) { org.StripeSubscriptionStatus = status; changed = true; }
        if (org.StripeReconciliationMissingSince is not null) { org.StripeReconciliationMissingSince = null; changed = true; } // self-heal

        if (changed)
        {
            org.BillingUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Reconcile: org {OrgId} → plan={Plan}, status={AccountStatus} (stripe {SubStatus}).",
                org.Id, org.Plan, org.AccountStatus, status);
        }
    }

    private async Task HandleMissingAsync(Organisation org, CancellationToken ct)
    {
        var deadSubId = org.StripeSubscriptionId;
        if (org.StripeReconciliationMissingSince is null)
        {
            org.StripeReconciliationMissingSince = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Reconcile: org {OrgId} subscription {SubId} not found in Stripe (resource_missing) — grace window started ({Days}d).",
                org.Id, deadSubId, GracePeriod.TotalDays);
            return;
        }

        if (DateTime.UtcNow - org.StripeReconciliationMissingSince.Value >= GracePeriod)
        {
            await DowngradeAsync(org, deadSubId!, ct);
        }
        else
        {
            _logger.LogInformation("Reconcile: org {OrgId} subscription still missing but within grace (since {Since:o}).",
                org.Id, org.StripeReconciliationMissingSince);
        }
    }

    private async Task DowngradeAsync(Organisation org, string deadSubId, CancellationToken ct)
    {
        var previousPlan = org.Plan;
        org.Plan = PlanConstants.Pilot;
        org.AccountStatus = AccountStatusConstants.ReadOnly;
        org.StripeSubscriptionId = null;               // (c) clear — org drops out of the sweep next run
        org.StripePriceId = null;                       // (c) clear
        org.StripeSubscriptionStatus = "canceled";      // forensic marker
        org.StripeReconciliationMissingSince = null;    // downgrade applied
        org.BillingUpdatedAt = DateTime.UtcNow;         // StripeCustomerId kept
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("Reconcile: downgraded org {OrgId} to frozen Pilot + read_only — dead/vanished subscription {DeadSubId}.",
            org.Id, deadSubId);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hadOrders = await _db.PurchaseOrders.AnyAsync(o => o.OrgId == org.Id && !o.IsSample && o.CreatedAt >= monthStart, ct);
        await _billing.EmitBillingCancelledAsync(org.Id, previousPlan, hadOrders, ct);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test ProcuLink.Api.Tests --filter FullyQualifiedName~StripeSubscriptionReconciliationServiceTests`
Expected: PASS — all 12 cases green. (If `FakeAnalyticsService.Captured` differs, adapt the assertion in case 4 to that double's public surface.)

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Services/IBillingReconciliationService.cs ProcuLink.Api/Services/StripeSubscriptionReconciliationService.cs ProcuLink.Api.Tests/Services/StripeSubscriptionReconciliationServiceTests.cs
git commit -m "feat(billing): Stripe subscription reconciliation service (idempotent, org-scoped, 3-day grace)"
```

---

### Task 4: `BillingReconciliationJob` sweep + registration

**Files:**
- Create: `ProcuLink.Worker/Jobs/BillingReconciliationJob.cs`
- Test: `ProcuLink.Api.Tests/Jobs/BillingReconciliationJobTests.cs`
- Modify: `ProcuLink.Worker/Worker.cs`; `ProcuLink.Worker/Program.cs`; `ProcuLink.Api/Program.cs`

**Interfaces:**
- Consumes: `IBillingReconciliationService.ReconcileOrgAsync` (Task 3).
- Produces: `BillingReconciliationJob.ExecuteAsync(CancellationToken) : Task`.

- [ ] **Step 1: Write the failing test** (sweep predicate + per-org isolation)

```csharp
// ProcuLink.Api.Tests/Jobs/BillingReconciliationJobTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

public class BillingReconciliationJobTests
{
    private sealed class RecordingReconciler : IBillingReconciliationService
    {
        public List<Guid> Reconciled { get; } = new();
        public HashSet<Guid> Throw { get; } = new();
        public Task ReconcileOrgAsync(Guid orgId, CancellationToken ct = default)
        {
            Reconciled.Add(orgId);
            if (Throw.Contains(orgId)) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Organisation Org(string? subId)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id = id, ClerkOrgId = $"org_{id:N}", Name = "n", Slug = $"s-{id:N}",
            Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            StripeSubscriptionId = subId, CreatedAt = DateTime.UtcNow, TrialStartedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task Sweep_OnlyReconcilesOrgsWithASubscriptionId()
    {
        var db = MakeDb();
        var withSub = Org("sub_a");
        var blankSub = Org("");
        var noSub = Org(null);
        db.Organisations.AddRange(withSub, blankSub, noSub);
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();
        var job = new BillingReconciliationJob(db, reconciler, NullLogger<BillingReconciliationJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().ContainSingle().Which.Should().Be(withSub.Id);
    }

    [Fact]
    public async Task Sweep_OneOrgThrowing_DoesNotAbortTheRest()
    {
        var db = MakeDb();
        var a = Org("sub_a"); var b = Org("sub_b"); var c = Org("sub_c");
        db.Organisations.AddRange(a, b, c);
        await db.SaveChangesAsync();
        var reconciler = new RecordingReconciler();
        reconciler.Throw.Add(b.Id);
        var job = new BillingReconciliationJob(db, reconciler, NullLogger<BillingReconciliationJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        reconciler.Reconciled.Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id },
            "every org is attempted even though one threw (per-org try/catch isolation)");
    }
}
```

Note: `ProcuLink.Api.Tests` already references the Worker project (it tests `EmailPollOrgJob` etc.), so `ProcuLink.Worker.Jobs` is resolvable here.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter FullyQualifiedName~BillingReconciliationJobTests`
Expected: FAIL — `BillingReconciliationJob` does not exist.

- [ ] **Step 3: Implement the job**

```csharp
// ProcuLink.Worker/Jobs/BillingReconciliationJob.cs
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring sweep (daily 02:00 UTC): reconciles every org that has a Stripe subscription id
/// against Stripe as source of truth. Safety net for missed subscription webhooks and stale/
/// test-mode ids. Per-org try/catch isolates one org's failure from the rest. Idempotent — the
/// underlying <see cref="IBillingReconciliationService"/> converges and a downgraded org clears
/// its subscription id, dropping out of this sweep.
/// </summary>
public sealed class BillingReconciliationJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBillingReconciliationService _reconciliation;
    private readonly ILogger<BillingReconciliationJob> _logger;

    public BillingReconciliationJob(
        ProcuLinkDbContext db,
        IBillingReconciliationService reconciliation,
        ILogger<BillingReconciliationJob> logger)
    {
        _db = db; _reconciliation = reconciliation; _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var ids = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.StripeSubscriptionId != null && o.StripeSubscriptionId != "")
            .Select(o => o.Id)
            .ToListAsync(ct);

        _logger.LogInformation("BillingReconciliationJob: {Count} org(s) with a Stripe subscription to reconcile.", ids.Count);

        var ok = 0;
        foreach (var id in ids)
        {
            try { await _reconciliation.ReconcileOrgAsync(id, ct); ok++; }
            catch (Exception ex) { _logger.LogError(ex, "BillingReconciliationJob: reconcile failed for org {OrgId}.", id); }
        }

        _logger.LogInformation("BillingReconciliationJob complete — {Ok}/{Total} org(s) reconciled without error.", ok, ids.Count);
    }
}
```

- [ ] **Step 4: Run the job tests**

Run: `dotnet test ProcuLink.Api.Tests --filter FullyQualifiedName~BillingReconciliationJobTests`
Expected: PASS — both cases green.

- [ ] **Step 5: Register the recurring job + DI**

In `ProcuLink.Worker/Worker.cs` `StartAsync`, after the `blob-retention-sweep` block (~:92):
```csharp
// Billing safety net: reconcile every org's plan/account_status against Stripe as source
// of truth (daily 02:00 UTC). Backstops missed subscription webhooks and stale/test-mode
// ids; downgrades vanished subscriptions to frozen Pilot + read_only after a 3-day grace.
_recurringJobs.AddOrUpdate<BillingReconciliationJob>(
    "billing-reconciliation",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 2 * * *");
```
Add `billing-reconciliation (daily 02:00 UTC)` to the final "Registered recurring jobs:" log line.

In `ProcuLink.Worker/Program.cs`, next to the `IBillingService` registration (~:176):
```csharp
builder.Services.AddScoped<IBillingReconciliationService, StripeSubscriptionReconciliationService>();
```
(Add `using ProcuLink.Api.Services;` / `using ProcuLink.Core.Services;` if not already imported.)
In `ProcuLink.Api/Program.cs`, add the same registration next to its `IBillingService` line (parity; keeps DI validation consistent).

- [ ] **Step 6: Build the Worker to prove DI + registration compile**

Run: `dotnet build ProcuLink.Worker`
Expected: Build succeeded (BillingReconciliationJob's deps — DbContext, IBillingReconciliationService, ILogger — all resolvable).

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Worker/Jobs/BillingReconciliationJob.cs ProcuLink.Api.Tests/Jobs/BillingReconciliationJobTests.cs ProcuLink.Worker/Worker.cs ProcuLink.Worker/Program.cs ProcuLink.Api/Program.cs
git commit -m "feat(billing): daily BillingReconciliationJob sweep + DI registration (0 2 * * *)"
```

---

### Task 5: Full-suite verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build ProcuLink.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the affected test projects**

Run: `dotnet test ProcuLink.Api.Tests`
Expected: PASS — new suites + `BillingControllerTests` all green. (Known-noise: the Docker-gated `TwoConcurrentRetries…` Postgres test may be skipped/flaky — that is pre-existing, not from this change.)

- [ ] **Step 3: Confirm no stray edits in the main checkout**

Run: `git -C "C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink" status --short`
Expected: empty (all work is in the worktree).

- [ ] **Step 4: Push the branch and check CI** (local green ≠ CI green — Windows dev, Linux CI)

Run: `git push -u origin claude/practical-swartz-03868d` then `gh run list --branch claude/practical-swartz-03868d --limit 3`
Expected: CI queued/green. Do NOT merge — founder-gated (LIVE billing).

---

## Self-Review

**Spec coverage:** Problem/goal → Tasks 3+4. Decision (a) frozen Pilot+read_only → `DowngradeAsync`. (b) 3-day grace + column → Task 2 + `HandleMissingAsync`. (c) clear sub+price keep customer → `DowngradeAsync`. (d) daily 02:00 → Task 4 Step 5. Stripe-key trap → service builds own client (Task 3 Step 4). Mapping extraction → Task 1. 404-vs-401 safety → `IsResourceMissing` + case 9 test. Idempotency → `changed` gating + cleared sub id + case 5/6/7/8. Org-scope → `Id == orgId` + case 10 + sweep predicate. Not-configured → case 11. All 13 spec test cases mapped (case 13 = re-running `BillingControllerTests` in Task 1 Step 5). No gaps.

**Placeholder scan:** Two items require recon confirmation before running, flagged inline (not placeholders — the code is written, only the exact `dotnet ef` command string in Task 2 Step 2 and the `StripeException` ctor in Task 3 Step 2 need verification against this repo / Stripe.net 51.1.0). Everything else is concrete.

**Type consistency:** `ReconcileOrgAsync(Guid, CancellationToken)` identical across interface, impl, job, and both test doubles. `MapPriceIdToPlan(IConfiguration, string?)` / `MapStatusToAccountStatus(string, string)` identical between helper, its tests, controller refactor, and service. `StripeReconciliationMissingSince` (DateTime?) identical across entity, migration, service, tests.
