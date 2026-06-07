using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// Proves the webhook firing job (<see cref="FireIntegrationTriggerJob"/>) sends through the
/// SSRF connect-time-revalidating client, not the plain named "delivery" client — closing the
/// same DNS-rebinding TOCTOU window the HTTP delivery dispatcher closes.
/// </summary>
public class FireIntegrationTriggerJobGuardTests
{
    private static OutboundRequestGuard MakeGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "false",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static IHttpClientFactory MakeFactory(out HttpClient plainClient)
    {
        plainClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var captured = plainClient;
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(captured);
        return factory.Object;
    }

    private static DeliveryEncryptionService MakeEnc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    [Fact]
    public void CreateSendClient_ReturnsGuardedClient_NotThePlainDeliveryClient()
    {
        var factory = MakeFactory(out var plainClient);
        // _db is not touched by CreateSendClient — the field is only read inside ExecuteAsync.
        var job = new FireIntegrationTriggerJob(
            null!, factory, MakeEnc(), MakeGuard(),
            NullLogger<FireIntegrationTriggerJob>.Instance);

        var sendClient = job.CreateSendClient();

        // It must NOT be the plain named-client (which has no connect-time SSRF callback) and it
        // must reuse the same instance across calls (one pooled SocketsHttpHandler).
        sendClient.Should().NotBeSameAs(plainClient);
        sendClient.Should().BeSameAs(job.CreateSendClient());
        // Timeout is mirrored from the named "delivery" client config.
        sendClient.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
