using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Pricing-fix tests for <see cref="StripeBillingService"/>:
///  • #1 NEVER block a real order on an active paid plan due to volume;
///  • over-cap usage is flagged + accrues EUR0.50/order overage, surfaced in the DTO;
///  • overage billing is idempotent (no double-bill on a repeated webhook key);
///  • Pilot-EXPIRED stays read-only (trial-ended, not a volume block);
///  • Integration effective limit is 1500;
///  • admin per-org override raises the effective limit.
/// Stripe is intentionally unconfigured (no SecretKey) so no live HTTP is made;
/// overage is still computed + recorded for auditing.
/// </summary>
public class StripeBillingServicePricingTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StripeBillingService MakeService(ProcuLinkDbContext db) =>
        new(db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), // no Stripe:SecretKey
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService());

    private static Organisation Org(
        string plan,
        string status,
        int? orderLimitOverride = null,
        DateTime? trialEndsAtOverride = null)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id                  = id,
            ClerkOrgId          = $"org_{id:N}",
            Name                = "Test Org",
            Slug                = $"test-{id:N}",
            Plan                = plan,
            AccountStatus       = status,
            CreatedAt           = DateTime.UtcNow.AddDays(-40),
            TrialStartedAt      = DateTime.UtcNow.AddDays(-40),
            OrderLimitOverride  = orderLimitOverride,
            TrialEndsAtOverride = trialEndsAtOverride,
        };
    }

    /// <summary>Adds <paramref name="count"/> month-to-date non-sample orders for the org.</summary>
    private static async Task SeedOrdersThisMonthAsync(ProcuLinkDbContext db, Guid orgId, int count)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Sup", CreatedAt = DateTime.UtcNow });
        for (var i = 0; i < count; i++)
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = $"PO-{Guid.NewGuid():N}",
                Status     = "delivered",
                OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
                Currency   = "EUR",
                CreatedAt  = monthStart.AddMinutes(i + 1),
                UpdatedAt  = monthStart.AddMinutes(i + 1),
            });
        }
        await db.SaveChangesAsync();
    }

    // ── #1 NEVER BLOCK an active paid plan on volume ──────────────────────

    [Fact]
    public async Task ActivePaidPlan_OverMonthlyCap_StillCanProcessOrders_AndIsFlaggedOverLimit()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // limit 150
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 155); // 5 over the cap

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.CanProcessOrders.Should().BeTrue("an active paid plan must never be volume-blocked");
        status.IsOrderLimitReached.Should().BeTrue();
        status.AtLimit.Should().BeTrue();
        status.OverageOrders.Should().Be(5);
    }

    [Fact]
    public async Task ActivePaidPlan_OverCap_CheckOrderLimit_ReturnsAllowed()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Operations, AccountStatusConstants.Active); // limit 500
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 510);

        var check = await MakeService(db).CheckOrderLimitAsync(org.Id);

        check.Allowed.Should().BeTrue("the order gate must never block a paid plan over its cap");
        check.PilotExpired.Should().BeFalse();
    }

    // ── overage math + surfacing ──────────────────────────────────────────

    [Fact]
    public async Task Overage_IsComputed_AsOrdersOverCapTimesFiftyCents()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // limit 150
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 160); // 10 over

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OverageOrders.Should().Be(10);
        status.OverageAmountEur.Should().Be(10 * 0.50m); // EUR5.00
    }

    [Fact]
    public async Task UnderCap_HasNoOverage_ButNearLimitFlagsAtEightyPercent()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // limit 150 → 80% = 120
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 120);

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OverageOrders.Should().Be(0);
        status.OverageAmountEur.Should().Be(0m);
        status.NearLimit.Should().BeTrue("120/150 is exactly 80%");
        status.AtLimit.Should().BeFalse();
        status.CanProcessOrders.Should().BeTrue();
    }

    // ── Pilot-expired stays read-only (trial-ended, NOT a volume block) ───

    [Fact]
    public async Task PilotExpired_OverTrialOrderCap_CannotProcess()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing); // pilot cap 20
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 25); // over the Pilot trial cap

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.IsTrialExpired.Should().BeTrue("Pilot past its order cap is trial-expired");
        status.CanProcessOrders.Should().BeFalse("a trial-ended Pilot is read-only, by design");
        // Pilot accrues NO overage — it is a hard trial cap, not a metered paid plan.
        status.OverageOrders.Should().Be(0);
        status.OverageAmountEur.Should().Be(0m);
    }

    [Fact]
    public async Task ReadOnlyPaidAccount_CannotProcess_EvenUnderCap()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.ReadOnly); // cancelled/frozen
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 5);

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.CanProcessOrders.Should().BeFalse("read-only status blocks processing — not a volume block");
    }

    // ── Integration effective limit is 1500 ───────────────────────────────

    [Fact]
    public void IntegrationOrderLimit_Is1500()
    {
        PlanConstants.GetOrderLimit(PlanConstants.Integration).Should().Be(1500);
    }

    [Fact]
    public async Task IntegrationPlan_ReportsEffectiveLimit1500()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Integration, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);
        status.OrderLimit.Should().Be(1500);
    }

    // ── admin override raises the effective limit ─────────────────────────

    [Fact]
    public async Task AdminOrderLimitOverride_RaisesEffectiveLimit_AndShrinksOverage()
    {
        var db = MakeDb();
        // Growth default 150, but admin granted 1000 headroom for this prospect.
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active, orderLimitOverride: 1000);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 200); // over the plan default, under the override

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrderLimit.Should().Be(1000, "the admin override replaces the plan default");
        status.OverageOrders.Should().Be(0, "200 < 1000 effective limit ⇒ no overage");
        status.AtLimit.Should().BeFalse();
        status.CanProcessOrders.Should().BeTrue();
    }

    [Fact]
    public async Task AdminTrialEndOverride_KeepsPilotAlive_PastDefaultWindow()
    {
        var db = MakeDb();
        // Trial started 40 days ago (default window long gone), but admin extended it.
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing,
            trialEndsAtOverride: DateTime.UtcNow.AddDays(20));
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 5); // under the pilot cap

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.IsTrialExpired.Should().BeFalse("the admin trial-end override keeps the Pilot alive");
        status.CanProcessOrders.Should().BeTrue();
    }

    // ── overage billing: idempotent (no double-bill) ──────────────────────

    [Fact]
    public async Task BillOverage_RecordsOnce_AndRepeatWithSameKeyDoesNotDoubleBill()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        var svc = MakeService(db);

        // First call bills 10 orders × €0.50 = €5.00 = 500 cents.
        var first = await svc.BillOverageForInvoiceAsync(org.Id, "in_period_jan", overageOrders: 10);
        first.AlreadyBilled.Should().BeFalse();
        first.OverageOrders.Should().Be(10);
        first.AmountCents.Should().Be(500);

        // Repeated webhook with the SAME key — must be a no-op, never a second charge.
        var second = await svc.BillOverageForInvoiceAsync(org.Id, "in_period_jan", overageOrders: 10);
        second.AlreadyBilled.Should().BeTrue("a replay of the same billing key must not double-bill");

        db.OverageBillingRecords.Count(r => r.OrgId == org.Id && r.BillingKey == "in_period_jan")
            .Should().Be(1, "exactly one ledger row per (org, billing key)");
    }

    [Fact]
    public async Task BillOverage_DifferentKeys_BillEachPeriodSeparately()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Operations, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        var svc = MakeService(db);

        await svc.BillOverageForInvoiceAsync(org.Id, "in_jan", overageOrders: 3);
        await svc.BillOverageForInvoiceAsync(org.Id, "in_feb", overageOrders: 7);

        db.OverageBillingRecords.Count(r => r.OrgId == org.Id).Should().Be(2);
    }

    [Fact]
    public async Task BillOverage_ZeroOverage_IsNoOp()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        var svc = MakeService(db);

        var result = await svc.BillOverageForInvoiceAsync(org.Id, "in_zero", overageOrders: 0);

        result.AmountCents.Should().Be(0);
        result.AlreadyBilled.Should().BeFalse();
        db.OverageBillingRecords.Should().BeEmpty("nothing to bill ⇒ no ledger row");
    }

    [Fact]
    public async Task BillOverage_WhenStripeUnconfigured_StillRecordsButDoesNotThrow()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        org.StripeCustomerId = null; // no Stripe customer
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        var svc = MakeService(db);

        var result = await svc.BillOverageForInvoiceAsync(org.Id, "in_unconfig", overageOrders: 4);

        result.AmountCents.Should().Be(200);
        result.StripeItemId.Should().BeNull("Stripe unconfigured ⇒ no invoice item, but the number is recorded");
        db.OverageBillingRecords.Should().ContainSingle(r => r.OrgId == org.Id && r.BillingKey == "in_unconfig");
    }

    // ── period overage compute helper ─────────────────────────────────────

    [Fact]
    public async Task ComputePeriodOverage_CountsOrdersOverCapInWindow()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, org.Id, 153); // 3 over

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(
            org.Id, monthStart, DateTime.UtcNow.AddDays(1));

        overage.Should().Be(3);
    }

    [Fact]
    public async Task ComputePeriodOverage_PilotAndEnterprise_AreZero()
    {
        var db = MakeDb();
        var pilot = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        var ent   = Org(PlanConstants.Enterprise, AccountStatusConstants.Active);
        db.Organisations.AddRange(pilot, ent);
        await db.SaveChangesAsync();
        await SeedOrdersThisMonthAsync(db, pilot.Id, 100);
        await SeedOrdersThisMonthAsync(db, ent.Id, 100_000);

        var svc = MakeService(db);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        (await svc.ComputePeriodOverageOrdersAsync(pilot.Id, monthStart, DateTime.UtcNow.AddDays(1)))
            .Should().Be(0, "Pilot has no metered overage");
        (await svc.ComputePeriodOverageOrdersAsync(ent.Id, monthStart, DateTime.UtcNow.AddDays(1)))
            .Should().Be(0, "Enterprise is custom-contracted, no overage");
    }
}
