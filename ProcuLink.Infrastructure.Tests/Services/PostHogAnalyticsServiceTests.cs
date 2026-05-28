using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class PostHogAnalyticsServiceTests
{
    [Fact]
    public async Task CaptureAsync_NoOps_WhenApiKeyMissing()
    {
        var opts = Options.Create(new PostHogOptions { ApiKey = null, Host = "https://eu.posthog.com" });
        var svc  = new PostHogAnalyticsService(opts, NullLogger<PostHogAnalyticsService>.Instance);

        // Should not throw, should not attempt network calls.
        await svc.CaptureAsync(
            organisationId: Guid.NewGuid(),
            userId: "user_123",
            eventName: "test_event",
            properties: new Dictionary<string, object?> { ["foo"] = "bar" });
    }

    [Fact]
    public async Task CaptureAsync_AlwaysIncludesOrganisationGroup_WhenKeyConfigured()
    {
        // Integration-shaped contract test: ensures we tag $groups.organisation so PostHog
        // cohort/funnel filtering works. Verified via the service's in-memory test sink.
        var opts = Options.Create(new PostHogOptions { ApiKey = "phc_test", Host = "https://eu.posthog.com" });
        var svc  = new PostHogAnalyticsService(opts, NullLogger<PostHogAnalyticsService>.Instance);

        var orgId = Guid.NewGuid();
        await svc.CaptureAsync(orgId, "user_abc", "first_supplier_added");

        var queued = svc.PeekTestQueue();
        Assert.Single(queued);
        Assert.Equal("first_supplier_added", queued[0].EventName);
        Assert.Equal("user_abc", queued[0].DistinctId);
        Assert.True(queued[0].Groups.ContainsKey("organisation"));
        Assert.Equal(orgId.ToString(), queued[0].Groups["organisation"]);
    }
}
