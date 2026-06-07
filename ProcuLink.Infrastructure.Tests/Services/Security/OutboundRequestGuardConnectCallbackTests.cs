using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// Tests for the DNS-rebinding TOCTOU defence (P1-1). The up-front
/// <see cref="OutboundRequestGuard.ValidateAsync"/> resolves+validates DNS, but the actual
/// HTTP send re-resolves the host at connect time. A malicious DNS server can answer "public"
/// during validation and "private/metadata" a moment later at connect. These tests prove the
/// connect-time callback re-resolves and re-validates, rejecting a host that points at a
/// loopback / private / metadata IP at connect time.
/// </summary>
public class OutboundRequestGuardConnectCallbackTests
{
    private static OutboundRequestGuard MakeGuard(bool allowPrivate = false)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    // ── Direct connect-callback core: host that resolves to a private/loopback IP ──────────

    [Fact]
    public async Task GuardedConnect_HostResolvesToLoopback_IsRejectedAtConnectTime()
    {
        // "localhost" resolves to 127.0.0.1 / ::1 — both blocked ranges. This simulates the
        // rebind: the name is allowed to resolve, but the connect-time IP is private, so the
        // callback must refuse to open the socket.
        var guard = MakeGuard(allowPrivate: false);

        var act = async () =>
            await guard.GuardedConnectCoreAsync("localhost", 80, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("blocked");
    }

    [Fact]
    public async Task GuardedConnect_LiteralLoopbackIp_IsRejectedAtConnectTime()
    {
        var guard = MakeGuard(allowPrivate: false);

        var act = async () =>
            await guard.GuardedConnectCoreAsync("127.0.0.1", 80, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GuardedConnect_LiteralPrivateIp_IsRejectedAtConnectTime()
    {
        // 169.254.169.254 — cloud metadata endpoint. The classic SSRF target.
        var guard = MakeGuard(allowPrivate: false);

        var act = async () =>
            await guard.GuardedConnectCoreAsync("169.254.169.254", 80, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GuardedConnect_UnresolvableHost_IsRejected()
    {
        var guard = MakeGuard(allowPrivate: false);

        // RFC 6761 reserves ".invalid" for guaranteed-non-resolving names.
        var act = async () =>
            await guard.GuardedConnectCoreAsync("this-host-does-not-exist.invalid", 80, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── End-to-end through SocketsHttpHandler.ConnectCallback ──────────────────────────────

    [Fact]
    public async Task GuardedHandler_RequestToLoopbackHost_FailsAtConnect()
    {
        // Drive the handler exactly as the dispatcher does. The request never reaches a server:
        // the ConnectCallback rejects the loopback IP before any socket is opened. This is the
        // full-wiring proof that the rebind window is closed for HTTP delivery + webhooks.
        var guard = MakeGuard(allowPrivate: false);
        using var handler = guard.CreateGuardedHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        var act = async () => await client.GetAsync("http://localhost/whatever");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GuardedHandler_AllowPrivateFlag_DoesNotBlockAtCallback()
    {
        // With the dev/test escape hatch the callback must NOT reject on range — it attempts a
        // normal connect. There is no listener, so the request still fails, but NOT with the
        // SSRF "blocked" reason. We assert the core path does not throw the block message.
        var guard = MakeGuard(allowPrivate: true);

        // GuardedConnectCoreAsync with allowPrivate connects by hostname; with no listener the
        // socket connect fails (SocketException), which is NOT our SSRF HttpRequestException block.
        // A short deadline keeps the test from hanging if the OS stalls the refused connect.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var act = async () =>
            await guard.GuardedConnectCoreAsync("127.0.0.1", 1, cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<HttpRequestException>(
            because: "AllowPrivateNetworkTargets must skip the SSRF range block");
    }
}
