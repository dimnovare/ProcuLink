using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using Stripe;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// A paying org that changes plan must change its LIVE subscription. Checkout always opened a
/// second subscription and nothing cancelled the first, so an upgrade billed both plans — and
/// because webhooks find the org by customer id, the old subscription's renewal, invoice and
/// cancellation kept acting on the org.
/// </summary>
public class PlanChangeOnLiveSubscriptionTests
{
    private const string LiveSubId = "sub_live";

    /// <summary>Records every Stripe call and answers each one with the org's live Growth subscription.</summary>
    private sealed class RecordingStripeClient : IStripeClient
    {
        public List<(HttpMethod Method, string Path, BaseOptions Options)> Calls { get; } = new();

        public string ApiBase => "https://api.stripe.invalid";
        public string ApiKey => "sk_test_fake";
        public string ClientId => "ca_fake";
        public string ConnectBase => "https://connect.stripe.invalid";
        public string FilesBase => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default) where T : IStripeEntity
        {
            Calls.Add((method, path, options));
            return Task.FromResult((T)(IStripeEntity)LiveSubscription("price_growth_test"));
        }

        public Task<Stream> RequestStreamingAsync(HttpMethod method, string path,
            BaseOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static Subscription LiveSubscription(string priceId, string id = LiveSubId) => new()
    {
        Id = id,
        CustomerId = "cus_paid",
        Status = "active",
        Items = new StripeList<SubscriptionItem>
        {
            Data = new List<SubscriptionItem>
            {
                new() { Id = "si_live", Price = new Price { Id = priceId }, CurrentPeriodStart = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) },
            },
        },
    };

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:GrowthPriceId"]     = "price_growth_test",
            ["Stripe:OperationsPriceId"] = "price_ops_test",
            ["Stripe:StarterPriceId"]    = "price_starter_test",
        }).Build();

    private static async Task<(ProcuLinkDbContext Db, Guid OrgId)> PaidGrowthOrgAsync()
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Paid Org", Slug = $"paid-{orgId:N}",
            Plan = PlanConstants.Growth, AccountStatus = AccountStatusConstants.Active,
            StripeCustomerId = "cus_paid", StripeSubscriptionId = LiveSubId,
        });
        await db.SaveChangesAsync();
        return (db, orgId);
    }

    [Fact]
    public async Task PaidToPaidUpgrade_UpdatesTheLiveSubscription_AndNeverOpensCheckout()
    {
        var (db, orgId) = await PaidGrowthOrgAsync();
        var stripe = new RecordingStripeClient();
        var svc = new StripeBillingService(db, Config(), NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService(), stripe);

        var url = await svc.CreateCheckoutSessionAsync(orgId, PlanConstants.Operations, "https://app.test", "monthly");

        url.Should().Be("https://app.test/welcome?upgraded=operations&interval=monthly");
        stripe.Calls.Should().NotContain(c => c.Path.StartsWith("/v1/checkout"), "a second Checkout is a second subscription");
        var update = stripe.Calls.Should().ContainSingle(c => c.Method == HttpMethod.Post).Subject;
        update.Path.Should().Be($"/v1/subscriptions/{LiveSubId}");
        var options = update.Options.Should().BeOfType<SubscriptionUpdateOptions>().Subject;
        options.Items.Should().ContainSingle(i => i.Id == "si_live" && i.Price == "price_ops_test");
        options.ProrationBehavior.Should().Be("always_invoice");
    }

    [Fact]
    public async Task CheaperPlanOnALiveSubscription_IsRefused_AndStripeIsNeverChanged()
    {
        var (db, orgId) = await PaidGrowthOrgAsync();
        var stripe = new RecordingStripeClient();
        var svc = new StripeBillingService(db, Config(), NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService(), stripe);

        var act = () => svc.CreateCheckoutSessionAsync(orgId, PlanConstants.Starter, "https://app.test", "monthly");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("plan_downgrade_not_allowed");
        stripe.Calls.Should().NotContain(c => c.Method == HttpMethod.Post, "€149 Growth must not move itself to €35 Starter");
    }

    [Fact]
    public async Task EventsForAnotherSubscriptionOfTheSameCustomer_NeverChangeTheOrg()
    {
        var (db, orgId) = await PaidGrowthOrgAsync();
        var ctrl = new BillingController(new Mock<IBillingService>().Object, new Mock<ICurrentTenantService>().Object,
            Config(), NullLogger<BillingController>.Instance, db, new Mock<IAiUsageTracker>().Object,
            new ProcuLink.Api.Services.Documents.DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new ProcuLink.Api.Services.Documents.DocumentEngineOptions())));
        var leftover = LiveSubscription("price_ops_test", id: "sub_leftover");

        await ctrl.HandleSubscriptionUpdatedAsync(leftover, DateTime.UtcNow, CancellationToken.None);
        await ctrl.HandleSubscriptionDeletedAsync(leftover, CancellationToken.None);

        var org = await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
        org.Plan.Should().Be(PlanConstants.Growth, "another subscription's renewal must not set this org's plan");
        org.StripeSubscriptionId.Should().Be(LiveSubId, "cancelling another subscription must not revert the org to Pilot");
    }
}
