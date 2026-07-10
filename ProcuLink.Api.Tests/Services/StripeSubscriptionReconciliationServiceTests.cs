using System.Linq;
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

/// <summary>
/// Unit tests for <see cref="StripeSubscriptionReconciliationService"/>. All Stripe traffic goes
/// through a faked <see cref="IStripeClient"/> (no HTTP, no prod). Covers healthy correction,
/// missed-upgrade repair, dunning, dead-sub downgrade, the 404 grace state machine, self-heal,
/// the safety-critical 401-never-downgrades rule, org-scope, and the unconfigured/no-sub no-ops.
/// </summary>
public class StripeSubscriptionReconciliationServiceTests
{
    // ── faked Stripe transport ──────────────────────────────────────────────
    // GetAsync(id) → the canned _getResponse (a Subscription or an Exception to throw);
    // ListAsync(customer) → a StripeList of _customerSubs (the re-subscribe adoption probe).
    private sealed class FakeStripeClient : IStripeClient
    {
        private readonly object _getResponse;
        private readonly List<Subscription> _customerSubs;
        public int Calls { get; private set; }      // GET /v1/subscriptions/{id}
        public int ListCalls { get; private set; }  // GET /v1/subscriptions (list by customer)
        public FakeStripeClient(object getResponse, IEnumerable<Subscription>? customerSubs = null)
        {
            _getResponse = getResponse;
            _customerSubs = customerSubs?.ToList() ?? new List<Subscription>();
        }

        public string ApiBase => "https://api.stripe.invalid";
        public string ApiKey => "sk_test_fake";
        public string ClientId => "ca_fake";
        public string ConnectBase => "https://connect.stripe.invalid";
        public string FilesBase => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default) where T : IStripeEntity
        {
            if (typeof(T) == typeof(StripeList<Subscription>))
            {
                ListCalls++;
                var list = new StripeList<Subscription> { Data = _customerSubs };
                return Task.FromResult((T)(IStripeEntity)list);
            }
            Calls++;
            if (_getResponse is Exception ex) throw ex;
            return Task.FromResult((T)(IStripeEntity)_getResponse);
        }

        public Task<System.IO.Stream> RequestStreamingAsync(HttpMethod method, string path,
            BaseOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static Subscription SubOf(string id, string status, string? priceId) => new()
    {
        Id = id,
        Status = status,
        Items = new StripeList<SubscriptionItem>
        {
            Data = new List<SubscriptionItem> { new() { Id = "si_1", Price = priceId is null ? null : new Price { Id = priceId } } }
        }
    };

    private static Subscription Sub(string status, string? priceId) => SubOf("sub_123", status, priceId);

    // Stripe.net 51.1.0 ctor: StripeException(HttpStatusCode, StripeError, string) — verified.
    private static StripeException NotFound() =>
        new(HttpStatusCode.NotFound,
            new StripeError { Code = "resource_missing", Type = "invalid_request_error", Message = "No such subscription: sub_123" },
            "No such subscription: sub_123");

    private static StripeException Unauthorized() =>
        new(HttpStatusCode.Unauthorized,
            new StripeError { Code = "api_key_expired", Type = "invalid_request_error", Message = "Invalid API Key provided" },
            "Invalid API Key provided");

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
        string? subId = "sub_123", string? priceId = "price_growth_m", DateTime? missingSince = null,
        string? subStatus = null, DateTime? billingUpdatedAt = null)
    {
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id = id, ClerkOrgId = $"org_{id:N}", Name = "Recon Org", Slug = $"recon-{id:N}",
            Plan = plan, AccountStatus = status, StripeCustomerId = "cus_keep",
            StripeSubscriptionId = subId, StripePriceId = priceId,
            StripeSubscriptionStatus = subStatus, StripeReconciliationMissingSince = missingSince,
            BillingUpdatedAt = billingUpdatedAt,
            CreatedAt = DateTime.UtcNow.AddDays(-30), TrialStartedAt = DateTime.UtcNow.AddDays(-30),
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task<Organisation> Reload(ProcuLinkDbContext db, Guid id) =>
        await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == id);

    // 1. drifted plan on a healthy active sub → corrected to Stripe truth
    [Fact]
    public async Task HealthyActive_DriftedPlan_CorrectedToStripe()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Operations, AccountStatusConstants.Active, priceId: "price_growth_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.PastDue);
    }

    // 4. canceled status (resolves) → immediate downgrade, ids cleared per decision (c)
    [Fact]
    public async Task CanceledStatus_ImmediateDowngrade_ClearsSubAndPriceKeepsCustomer()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("canceled", "price_growth_m")), out var analytics);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        after.StripeSubscriptionId.Should().BeNull();
        after.StripePriceId.Should().BeNull();
        after.StripeSubscriptionStatus.Should().Be("canceled");
        after.StripeCustomerId.Should().Be("cus_keep");
        after.StripeReconciliationMissingSince.Should().BeNull();
        analytics.CapturedEvents.Should().Contain(e => e.EventName == "billing_cancelled");
    }

    // 5. 404 first run → MissingSince set, NO plan change
    [Fact]
    public async Task Missing_FirstRun_SetsMarker_NoDowngrade()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
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
        var after = await Reload(db, org.Id);
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
        var otherAfter = await Reload(db, other.Id);
        otherAfter.Plan.Should().Be(PlanConstants.Operations);
        otherAfter.AccountStatus.Should().Be(AccountStatusConstants.Active);
        otherAfter.StripeSubscriptionId.Should().Be("sub_123");
    }

    // 11. Stripe not configured → no-op, Stripe never called
    [Fact]
    public async Task StripeNotConfigured_NoOp()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var fake = new FakeStripeClient(NotFound());
        var svc = MakeSvc(db, Config(stripeConfigured: false), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
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

    // Idempotency: running a downgrade twice is a stable no-op the second time (sub id already cleared).
    [Fact]
    public async Task Downgrade_IsIdempotent_SecondRunNoOp()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-4));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        await svc.ReconcileOrgAsync(org.Id); // sub id now null → early return, no throw
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.StripeSubscriptionId.Should().BeNull();
    }

    // ── Review coverage gaps (T1–T8) ─────────────────────────────────────────

    // T1: incomplete_expired (a never-activated sub) → immediate downgrade like canceled.
    [Fact]
    public async Task IncompleteExpired_ImmediateDowngrade()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("incomplete_expired", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        after.StripeSubscriptionId.Should().BeNull();
    }

    // T3: unpaid → PastDue, plan kept (dunning, not downgrade).
    [Fact]
    public async Task Unpaid_KeepsPlan_SetsPastDue()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Operations, AccountStatusConstants.Active, priceId: "price_ops_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("unpaid", "price_ops_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Operations);
        after.AccountStatus.Should().Be(AccountStatusConstants.PastDue);
    }

    // T4: an unmapped resolving status (e.g. 'incomplete') leaves plan+status unchanged
    // but STILL self-heals a stale missing marker (the sub resolves, so it is not missing).
    [Fact]
    public async Task UnmappedStatus_KeepsPlanAndStatus_ButClearsMissingMarker()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-1));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("incomplete", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
        after.StripeReconciliationMissingSince.Should().BeNull("a resolving sub is not missing");
    }

    // T2: a healthy sub returning a null price id must NOT null out the stored StripePriceId.
    [Fact]
    public async Task HealthyActive_NullPriceId_DoesNotClearStoredPrice()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, priceId: "price_growth_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", priceId: null)), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.StripePriceId.Should().Be("price_growth_m", "a live sub with no price must never null out the stored price");
        after.Plan.Should().Be(PlanConstants.Growth, "no mapped price → keep existing plan");
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
    }

    // T5: when nothing drifted, the no-write path must not bump BillingUpdatedAt.
    [Fact]
    public async Task NoDrift_DoesNotBumpBillingUpdatedAt()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            priceId: "price_growth_m", subStatus: "active", billingUpdatedAt: null);
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("active", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.BillingUpdatedAt.Should().BeNull("nothing changed, so no write and no timestamp bump");
    }

    // T6: the billing_cancelled payload carries the pre-downgrade plan and the had-orders flag.
    [Fact]
    public async Task Downgrade_EmitsBillingCancelled_WithPreviousPlanAndHadOrders()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Integration, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow.AddDays(-4));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out var analytics);
        await svc.ReconcileOrgAsync(org.Id);
        var ev = analytics.CapturedEvents.Should().ContainSingle(e => e.EventName == "billing_cancelled").Subject;
        ev.OrgId.Should().Be(org.Id);
        ev.Properties["previous_plan"].Should().Be(PlanConstants.Integration);
        ev.Properties["had_orders_this_month"].Should().Be(false);
    }

    // T7: the grace boundary is inclusive ('>= 3 days') — exactly 3 days past → downgrade.
    [Fact]
    public async Task Missing_ExactlyAtGraceBoundary_Downgrades()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            missingSince: DateTime.UtcNow - StripeSubscriptionReconciliationService.GracePeriod - TimeSpan.FromSeconds(1));
        var svc = MakeSvc(db, Config(), new FakeStripeClient(NotFound()), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.StripeSubscriptionId.Should().BeNull();
    }

    // T8: a resolving 'trialing' sub sets AccountStatus=Trialing through the service.
    [Fact]
    public async Task HealthyTrialing_SetsTrialing()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, priceId: "price_growth_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("trialing", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Growth);
        after.AccountStatus.Should().Be(AccountStatusConstants.Trialing);
    }

    // ── Founder follow-ups: re-subscribe adoption (B2) + paused→read_only (B3) ─

    // B2: a dead (canceled) stored sub, but the CUSTOMER has a newer active sub → adopt it,
    // do NOT downgrade. Restores a re-subscribed customer even though the checkout webhook was missed.
    [Fact]
    public async Task DeadSub_CustomerHasActiveSub_AdoptsInsteadOfDowngrade()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Pilot, AccountStatusConstants.ReadOnly, subId: "sub_old", priceId: null);
        var fake = new FakeStripeClient(
            SubOf("sub_old", "canceled", "price_growth_m"),
            customerSubs: new[] { SubOf("sub_new", "active", "price_ops_m") });
        var svc = MakeSvc(db, Config(), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.StripeSubscriptionId.Should().Be("sub_new", "adopt the re-subscribe rather than freeze a paying customer");
        after.Plan.Should().Be(PlanConstants.Operations);
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
        after.StripePriceId.Should().Be("price_ops_m");
        fake.ListCalls.Should().Be(1);
    }

    // B2: a past-grace MISSING (404) sub, but the customer has a newer active sub → adopt.
    [Fact]
    public async Task MissingPastGrace_CustomerHasActiveSub_AdoptsInsteadOfDowngrade()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active,
            subId: "sub_old", missingSince: DateTime.UtcNow.AddDays(-4));
        var fake = new FakeStripeClient(NotFound(), customerSubs: new[] { SubOf("sub_new", "active", "price_growth_m") });
        var svc = MakeSvc(db, Config(), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.StripeSubscriptionId.Should().Be("sub_new");
        after.AccountStatus.Should().Be(AccountStatusConstants.Active);
        after.StripeReconciliationMissingSince.Should().BeNull();
    }

    // B2: a dead sub and the customer has ONLY non-active subs → adoption declines, downgrade proceeds.
    [Fact]
    public async Task DeadSub_CustomerHasNoActiveSub_StillDowngrades()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, subId: "sub_old");
        var fake = new FakeStripeClient(
            SubOf("sub_old", "canceled", "price_growth_m"),
            customerSubs: new[] { SubOf("sub_dead2", "canceled", "price_growth_m") });
        var svc = MakeSvc(db, Config(), fake, out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Pilot);
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        after.StripeSubscriptionId.Should().BeNull();
        fake.ListCalls.Should().Be(1, "adoption is attempted before downgrading");
    }

    // B3: a resolving 'paused' sub → account_status read_only, plan kept.
    [Fact]
    public async Task PausedStatus_SetsReadOnly_KeepsPlan()
    {
        var db = MakeDb();
        var org = await AddOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Active, priceId: "price_growth_m");
        var svc = MakeSvc(db, Config(), new FakeStripeClient(Sub("paused", "price_growth_m")), out _);
        await svc.ReconcileOrgAsync(org.Id);
        var after = await Reload(db, org.Id);
        after.Plan.Should().Be(PlanConstants.Growth, "paused keeps the plan");
        after.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
    }
}
