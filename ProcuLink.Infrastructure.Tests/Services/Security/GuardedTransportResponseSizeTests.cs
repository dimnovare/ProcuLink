using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// Proves a guarded outbound call cannot buffer an unbounded response body.
///
/// <para><b>What was open.</b> <c>MaxResponseContentBufferSize</c> was set nowhere in the
/// solution, so every outbound <c>ReadAsStringAsync</c> fell back to the framework's 2 GB default.
/// The 8,000-character bound at <c>DeliveryAttempt.MaxResponseBodyLength</c> applies at PERSIST
/// time — the body is already fully in memory by then. A supplier streaming a multi-gigabyte
/// response therefore OOMs a worker running ten concurrent jobs.</para>
///
/// <para>These tests drive a real socket against a real server that answers hostilely in both
/// directions a server can lie: declaring an enormous <c>Content-Length</c>, and declaring none at
/// all and then never stopping.</para>
/// </summary>
public class GuardedTransportResponseSizeTests
{
    private const long TinyCap = 64 * 1024;

    private static OutboundRequestGuard LoopbackGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    /// <summary>The cap violation may arrive wrapped by HttpClient's own buffering.</summary>
    private static bool IsCapViolation(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is OutboundResponseTooLargeException) return true;
        return false;
    }

    [Fact]
    public async Task ADeclaredOversizeContentLength_IsRefusedBeforeTheBodyIsRead()
    {
        using var server = StubHttpServer.Start(StubHttpServer.DeclaresOversize(4L * 1024 * 1024 * 1024));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler(maxResponseBytes: TinyCap);
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync($"{server.BaseUrl}/huge");

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        IsCapViolation(thrown).Should().BeTrue(
            "a 4 GB declared length must be refused on the header alone, not discovered by reading it");
    }

    [Fact]
    public async Task AnUndeclaredEndlessBody_TripsTheCapMidRead()
    {
        // No Content-Length at all — connection-delimited, so the only way to bound it is to count
        // the bytes as they arrive.
        using var server = StubHttpServer.Start(StubHttpServer.Flood(TinyCap * 64));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler(maxResponseBytes: TinyCap);
        using var client = new HttpClient(handler);

        var act = () => client.GetStringAsync($"{server.BaseUrl}/endless");

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        IsCapViolation(thrown).Should().BeTrue(
            "a server that never stops sending must be cut off at the cap, not buffered whole");
    }

    [Fact]
    public async Task AResponseUnderTheCap_IsReturnedIntact()
    {
        var body = new string('x', (int)TinyCap - 512);
        using var server = StubHttpServer.Start(StubHttpServer.Ok(body));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler(maxResponseBytes: TinyCap);
        using var client = new HttpClient(handler);

        var received = await client.GetStringAsync($"{server.BaseUrl}/normal");

        received.Should().Be(body, "the cap must not truncate or corrupt a legitimate response");
    }

    [Fact]
    public async Task ARequestMayOptUpToALargerCap()
    {
        var body = new string('y', (int)TinyCap * 4);
        using var server = StubHttpServer.Start(StubHttpServer.Ok(body));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler(maxResponseBytes: TinyCap);
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/big");
        request.Options.Set(ResponseSizeLimitingHandler.MaxResponseBytesOption, TinyCap * 16);

        var response = await client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be(body,
            "the catalog download relies on this opt-up to fetch a legitimately large feed");
    }

    [Fact]
    public async Task ARequestCannotOptDownBelowNothing_AndAnInvalidOptUpFallsBackToTheDefault()
    {
        using var server = StubHttpServer.Start(StubHttpServer.Flood(TinyCap * 64));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler(maxResponseBytes: TinyCap);
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/endless");
        request.Options.Set(ResponseSizeLimitingHandler.MaxResponseBytesOption, 0);  // nonsense value

        var act = () => client.SendAsync(request);

        IsCapViolation((await act.Should().ThrowAsync<Exception>()).Which).Should().BeTrue(
            "a zero/negative override must fall back to the configured default, never to no cap");
    }

    [Fact]
    public async Task HttpDelivery_AgainstAFloodingSupplier_FailsCleanlyAtTheProductionCap()
    {
        // No cap argument anywhere: this is the dispatcher's own guarded client at the real
        // production default. The supplier answers with 4 MB more than the cap allows.
        using var server = StubHttpServer.Start(
            StubHttpServer.Flood(OutboundResponseLimits.DefaultMaxResponseBytes + 4L * 1024 * 1024));

        var services = new ServiceCollection();
        services.AddHttpClient("delivery", c => c.Timeout = TimeSpan.FromSeconds(60));
        var dispatcher = new HttpDeliveryDispatcher(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            LoopbackGuard(),
            NullLogger<HttpDeliveryDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("<order id=\"PO-1\" />"),
            "PO-1.xml",
            "application/xml",
            new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(),
                OrgId = Guid.NewGuid(),
                SupplierId = Guid.NewGuid(),
                Protocol = DeliveryProtocolConstants.Http,
                AutoDeliver = true,
                ConfigJson = JsonSerializer.Serialize(
                    new { url = $"{server.BaseUrl}/deliver", method = "POST", timeoutSeconds = 60 }),
                EncryptedCredentials = string.Empty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            string.Empty,
            default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("HTTP delivery failed before receiving a response.",
            "the cap surfaces through the existing catch as a clean, enumerated failure — not a crash");
    }

    [Fact]
    public void TheDefaultCap_LeavesRoomForTheLargestBodyTheAwsSdkPathCanReceive()
    {
        // The AWS SDK builds its own requests through the guarded transport, so it can never set
        // the per-request opt-up. The largest body it legitimately receives is an S3 ingress
        // object, bounded at IngressLimits.MaxFileBytes. Tightening the default below that would
        // break S3 ingress silently; this test is the reason written down.
        OutboundResponseLimits.DefaultMaxResponseBytes.Should().BeGreaterThan(
            IngressLimits.MaxFileBytes,
            "an S3 ingress object at the ingress ceiling must still fit through the guarded transport");
    }
}
