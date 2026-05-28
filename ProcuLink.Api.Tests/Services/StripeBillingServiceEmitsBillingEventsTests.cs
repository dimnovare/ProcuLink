using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Api.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Verifies the billing analytics emitter methods on <see cref="StripeBillingService"/>.
/// The emitters do not touch the database; they delegate to <see cref="Core.Services.IAnalyticsService"/>.
/// </summary>
public class StripeBillingServiceEmitsBillingEventsTests
{
    [Fact]
    public async Task EmitBillingUpgradedAsync_CapturesEvent_WithExpectedProperties()
    {
        var (svc, analytics) = CreateService();
        var orgId = Guid.NewGuid();

        await svc.EmitBillingUpgradedAsync(orgId, "pilot", "growth", "cs_test_123");

        analytics.CapturedEvents.Should().HaveCount(1);
        var ev = analytics.CapturedEvents[0];
        ev.OrgId.Should().Be(orgId);
        ev.UserId.Should().BeNull();
        ev.EventName.Should().Be("billing_upgraded");
        ev.Properties["from_plan"].Should().Be("pilot");
        ev.Properties["to_plan"].Should().Be("growth");
        ev.Properties["stripe_session_id"].Should().Be("cs_test_123");
    }

    [Fact]
    public async Task EmitBillingDowngradedAsync_CapturesEvent_WithExpectedProperties()
    {
        var (svc, analytics) = CreateService();
        var orgId = Guid.NewGuid();

        await svc.EmitBillingDowngradedAsync(orgId, "operations", "growth");

        analytics.CapturedEvents.Should().HaveCount(1);
        var ev = analytics.CapturedEvents[0];
        ev.OrgId.Should().Be(orgId);
        ev.UserId.Should().BeNull();
        ev.EventName.Should().Be("billing_downgraded");
        ev.Properties["from_plan"].Should().Be("operations");
        ev.Properties["to_plan"].Should().Be("growth");
    }

    [Fact]
    public async Task EmitBillingCancelledAsync_CapturesEvent_WithExpectedProperties()
    {
        var (svc, analytics) = CreateService();
        var orgId = Guid.NewGuid();

        await svc.EmitBillingCancelledAsync(orgId, "growth", hadOrdersThisMonth: true);

        analytics.CapturedEvents.Should().HaveCount(1);
        var ev = analytics.CapturedEvents[0];
        ev.OrgId.Should().Be(orgId);
        ev.UserId.Should().BeNull();
        ev.EventName.Should().Be("billing_cancelled");
        ev.Properties["previous_plan"].Should().Be("growth");
        ev.Properties["had_orders_this_month"].Should().Be(true);
    }

    private static (StripeBillingService Service, FakeAnalyticsService Analytics) CreateService()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ProcuLinkDbContext(options);

        var config = new ConfigurationBuilder().Build();
        var analytics = new FakeAnalyticsService();

        var svc = new StripeBillingService(
            db,
            config,
            NullLogger<StripeBillingService>.Instance,
            analytics);

        return (svc, analytics);
    }
}
