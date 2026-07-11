using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Every PRODUCTION write point of <see cref="Organisation.Plan"/> /
/// <see cref="Organisation.OrderLimitOverride"/> must leave an org_plan_history row
/// (via the ProcuLinkDbContext SaveChanges chokepoint — these tests run the REAL
/// handlers against the full EF model, not a reduced harness):
///  • checkout.session.completed (BillingController.HandleCheckoutCompletedAsync);
///  • customer.subscription.updated plan mapping (HandleSubscriptionUpdatedAsync);
///  • customer.subscription.deleted revert-to-Pilot (HandleSubscriptionDeletedAsync);
///  • admin limits endpoint (AdminController.SetOrganisationLimits);
///  • MarkPilotStartedAsync appends NOTHING when the plan does not actually change.
/// </summary>
public class OrgPlanHistoryWritePointTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration MakeConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:GrowthPriceId"]            = "price_growth_test",
                ["Stripe:GrowthYearlyPriceId"]      = "price_growth_yearly_test",
                ["Stripe:OperationsPriceId"]        = "price_ops_test",
                ["Stripe:OperationsYearlyPriceId"]  = "price_ops_yearly_test",
                ["Stripe:IntegrationPriceId"]       = "price_int_test",
                ["Stripe:DistributorPriceId"]       = "price_dist_test",
            })
            .Build();

    private static BillingController MakeBillingController(ProcuLinkDbContext db) =>
        new(new Mock<IBillingService>().Object,
            Mock.Of<ICurrentTenantService>(),
            MakeConfig(),
            NullLogger<BillingController>.Instance,
            db,
            new Mock<IAiUsageTracker>().Object);

    private static AdminController MakeAdminController(ProcuLinkDbContext db) =>
        new(db,
            new Mock<IBillingService>().Object,
            MakeConfig(),
            NullLogger<AdminController>.Instance,
            new Mock<IDataErasureService>().Object);

    private static async Task<Organisation> AddOrgAsync(
        ProcuLinkDbContext db, string plan, string status, string? stripeCustomerId = null)
    {
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id                   = id,
            ClerkOrgId           = $"org_{id:N}",
            Name                 = "Write Point Org",
            Slug                 = $"wp-{id:N}",
            Plan                 = plan,
            AccountStatus        = status,
            StripeCustomerId     = stripeCustomerId,
            StripeSubscriptionId = stripeCustomerId is null ? null : "sub_wp_test",
            CreatedAt            = DateTime.UtcNow.AddDays(-30),
            TrialStartedAt       = DateTime.UtcNow.AddDays(-30),
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task<List<OrgPlanHistory>> HistoryAsync(ProcuLinkDbContext db, Guid orgId) =>
        await db.OrgPlanHistories
            .Where(h => h.OrgId == orgId)
            .OrderBy(h => h.EffectiveFrom)
            .ToListAsync();

    // ── checkout.session.completed ──────────────────────────────────────────

    [Fact]
    public async Task HandleCheckoutCompleted_PlanUpgrade_AppendsHistoryRow()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Pilot, AccountStatusConstants.Trialing);
        var ctrl = MakeBillingController(db);

        var session = new Stripe.Checkout.Session
        {
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = org.Id.ToString(),
                ["plan"]   = PlanConstants.Growth,
            },
            Id             = "cs_history_test",
            SubscriptionId = null, // offline — no Stripe HTTP
            CustomerId     = "cus_history_test",
        };

        await ctrl.HandleCheckoutCompletedAsync(session, CancellationToken.None);

        var rows = await HistoryAsync(db, org.Id);
        rows.Should().HaveCount(2, "creation baseline + the upgrade row");
        rows[0].Plan.Should().Be(PlanConstants.Pilot);
        rows[^1].Plan.Should().Be(PlanConstants.Growth, "the upgrade must be recorded as-of now");
    }

    // ── customer.subscription.updated (webhook plan mapping) ────────────────

    [Fact]
    public async Task HandleSubscriptionUpdated_PlanChange_AppendsHistoryRow()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Operations, AccountStatusConstants.Active, "cus_sub_upd");
        var ctrl = MakeBillingController(db);

        var sub = new Stripe.Subscription
        {
            Id         = "sub_wp_test",
            CustomerId = "cus_sub_upd",
            Status     = "active",
            Items      = new Stripe.StripeList<Stripe.SubscriptionItem>
            {
                Data = new List<Stripe.SubscriptionItem>
                {
                    new() { Price = new Stripe.Price { Id = "price_growth_test" } }, // downgrade → growth
                },
            },
        };

        await ctrl.HandleSubscriptionUpdatedAsync(sub, DateTime.UtcNow, CancellationToken.None);

        var rows = await HistoryAsync(db, org.Id);
        rows.Should().HaveCount(2, "creation baseline + the downgrade row");
        rows[0].Plan.Should().Be(PlanConstants.Operations);
        rows[^1].Plan.Should().Be(PlanConstants.Growth,
            "the mid-cycle downgrade must be recorded so PAST windows keep metering at Operations");
    }

    [Fact]
    public async Task HandleSubscriptionUpdated_StatusOnlyChange_AppendsNothing()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, "cus_status_only");
        var ctrl = MakeBillingController(db);

        var sub = new Stripe.Subscription
        {
            Id         = "sub_wp_test",
            CustomerId = "cus_status_only",
            Status     = "past_due",
            Items      = new Stripe.StripeList<Stripe.SubscriptionItem>
            {
                Data = new List<Stripe.SubscriptionItem>(), // no price → plan unchanged
            },
        };

        await ctrl.HandleSubscriptionUpdatedAsync(sub, DateTime.UtcNow, CancellationToken.None);

        (await HistoryAsync(db, org.Id)).Should().HaveCount(1,
            "a status-only subscription update does not change the plan, so no history row");
    }

    // ── customer.subscription.deleted (revert to Pilot) ─────────────────────

    [Fact]
    public async Task HandleSubscriptionDeleted_RevertToPilot_AppendsHistoryRow()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, "cus_sub_del");
        var ctrl = MakeBillingController(db);

        var sub = new Stripe.Subscription
        {
            Id         = "sub_wp_test",
            CustomerId = "cus_sub_del",
        };

        await ctrl.HandleSubscriptionDeletedAsync(sub, CancellationToken.None);

        var rows = await HistoryAsync(db, org.Id);
        rows.Should().HaveCount(2, "creation baseline + the cancellation row");
        rows[^1].Plan.Should().Be(PlanConstants.Pilot,
            "cancellation reverts to Pilot — recorded so the final paid period still meters at Growth");
    }

    // ── admin limits endpoint (order-limit override) ────────────────────────

    [Fact]
    public async Task AdminSetLimits_SettingOrderLimitOverride_AppendsHistoryRow()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var ctrl = MakeAdminController(db);

        await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(OrderLimitOverride: 1000), CancellationToken.None);

        var rows = await HistoryAsync(db, org.Id);
        rows.Should().HaveCount(2, "creation baseline + the override row");
        rows[^1].OrderLimitOverride.Should().Be(1000);
        rows[^1].Plan.Should().Be(PlanConstants.Growth);
    }

    [Fact]
    public async Task AdminSetLimits_ClearingOrderLimitOverride_AppendsHistoryRow()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var ctrl = MakeAdminController(db);
        await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(OrderLimitOverride: 1000), CancellationToken.None);

        // Distinct EffectiveFrom timestamps for deterministic ordering below.
        await Task.Delay(30);

        await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(ClearOrderLimit: true), CancellationToken.None);

        var rows = await HistoryAsync(db, org.Id);
        rows.Should().HaveCount(3, "baseline + set-override row + clear-override row");
        rows[^1].OrderLimitOverride.Should().BeNull("clearing the override is itself a metering change");
    }

    [Fact]
    public async Task AdminSetLimits_SupplierLimitOnly_AppendsNothing()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var ctrl = MakeAdminController(db);

        await ctrl.SetOrganisationLimits(
            org.Id, new SetOrgLimitsRequest(SupplierLimitOverride: 50), CancellationToken.None);

        (await HistoryAsync(db, org.Id)).Should().HaveCount(1,
            "the supplier limit does not affect order overage metering — no history row");
    }

    // ── MarkPilotStartedAsync (plan write that is usually a no-op change) ───

    [Fact]
    public async Task MarkPilotStarted_OnAlreadyPilotOrg_AppendsNothing()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Pilot, AccountStatusConstants.Trialing);
        var svc = new StripeBillingService(
            db,
            MakeConfig(),
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService());

        await svc.MarkPilotStartedAsync(org.Id);

        (await HistoryAsync(db, org.Id)).Should().HaveCount(1,
            "re-asserting Plan=pilot on an already-Pilot org is not a plan change");
    }
}
