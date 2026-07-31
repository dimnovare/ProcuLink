using System.Reflection;
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
        var ex = await Record.ExceptionAsync(() => svc.CaptureAsync(
            organisationId: Guid.NewGuid(),
            userId: "user_123",
            eventName: "test_event",
            properties: new Dictionary<string, object?> { ["foo"] = "bar" }));

        Assert.Null(ex);

        // "No-ops" is a claim about the NETWORK, and the PostHog SDK client is the only thing in
        // this service that can open a socket. There is no HttpMessageHandler to count requests on
        // — the ctor takes IOptions + ILogger and news the SDK client internally — so the closest
        // observable to "zero requests were sent" is that no client was ever constructed.
        var clientField = typeof(PostHogAnalyticsService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(clientField); // renamed field => re-point this assertion, do not drop the claim
        Assert.Null(clientField!.GetValue(svc));

        // The same claim black-box, so it survives a rename: with no key the service must
        // short-circuit BEFORE it touches the host. A host string that cannot even be parsed as a
        // URI proves it — construction and capture stay quiet only because the client path, which
        // is what would do `new Uri(Host)`, is never entered.
        var unusableHost = Options.Create(new PostHogOptions { ApiKey = null, Host = "not-a-uri" });
        var exUnusableHost = await Record.ExceptionAsync(() =>
        {
            var quiet = new PostHogAnalyticsService(
                unusableHost, NullLogger<PostHogAnalyticsService>.Instance);
            return quiet.CaptureAsync(Guid.NewGuid(), "user_123", "test_event");
        });

        Assert.Null(exUnusableHost);
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
