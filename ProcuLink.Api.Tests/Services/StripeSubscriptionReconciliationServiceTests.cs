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
    // ── faked Stripe transport: returns a canned Subscription or throws ─────
    private sealed class FakeStripeClient : IStripeClient
    {
        private readonly object _response; // a Stripe.Subscription to return, or an Exception to throw
        public int Calls { get; private set; }
        public FakeStripeClient(object response) => _response = response;

        public string ApiBase => "https://api.stripe.invalid";
        public string ApiKey => "sk_test_fake";
        public string ClientId => "ca_fake";
        public string ConnectBase => "https://connect.stripe.invalid";
        public string FilesBase => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default) where T : IStripeEntity
        {
            Calls++;
            if (_response is Exception ex) throw ex;
            return Task.FromResult((T)(IStripeEntity)_response);
        }

        public Task<System.IO.Stream> RequestStreamingAsync(HttpMethod method, string path,
            BaseOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static Subscription Sub(string status, string? priceId) => new()
    {
        Id = "sub_123",
        Status = status,
        Items = new StripeList<SubscriptionItem>
        {
            Data = new List<SubscriptionItem> { new() { Id = "si_1", Price = priceId is null ? null : new Price { Id = priceId } } }
        }
    };

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
}
