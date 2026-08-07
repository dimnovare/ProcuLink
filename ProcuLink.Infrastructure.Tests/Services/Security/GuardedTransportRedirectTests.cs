using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Erp;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// Proves the guarded outbound transport does not follow HTTP redirects, and pins the runtime
/// behaviour that makes following one dangerous here.
///
/// <para><b>What was open.</b> <see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/> set only
/// a <c>ConnectCallback</c>. <c>SocketsHttpHandler.AllowAutoRedirect</c> defaults to <c>true</c>, so
/// every guarded call followed redirects. The connect callback re-validates each hop's IP, so a
/// redirect to an <i>internal</i> address was already blocked — but a redirect to any <i>public</i>
/// host was followed with no second pass through
/// <see cref="OutboundRequestGuard.ValidateAsync"/> and no same-origin check.</para>
///
/// <para><b>Why that is worse than a normal SSRF nit here.</b> The credentials on two of these
/// channels are not in the <c>Authorization</c> header, which is the only thing the runtime strips:
/// <see cref="DirectoConnector"/> puts <c>user</c>/<c>password</c> in the form <i>body</i>
/// (DirectoConnector.cs:64), and the <c>apikey</c> auth mode puts the secret in a
/// <i>custom header</i> (HttpAuthApplier.cs:68). A supplier endpoint — or a hijacked or expired
/// supplier domain — answering <c>307 Location: https://attacker.example/</c> therefore received
/// the purchase order and the ERP credentials.</para>
///
/// <para><b>Runtime facts these tests establish</b> (measured here, not quoted from documentation —
/// see the three <c>Runtime_</c> tests):</para>
/// <list type="bullet">
///   <item><b>307</b> preserves the method AND replays the body verbatim to the redirect target.</item>
///   <item>A <b>custom header</b> (e.g. <c>X-Api-Key</c>) is replayed on every redirect, same host
///     or not. Only <c>Authorization</c> is dropped, and only when the host <i>name</i> differs.</item>
///   <item><b>302</b> from a POST is downgraded to a GET and the body IS dropped — so the exposure
///     differs by status code, and only 307/308 replay the body. The custom header still crosses.</item>
/// </list>
/// </summary>
public class GuardedTransportRedirectTests
{
    private const string SecretBody = "xmldata=%3Corder%2F%3E&user=apiuser&password=hunter2";
    private const string SecretHeader = "sk-live-supplier-key";

    /// <summary>
    /// The guard configured for loopback tests. 127.0.0.1 is a private target, so without this the
    /// SSRF range check would block the connection before any redirect could happen and the test
    /// would prove nothing about redirects.
    /// </summary>
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

    private static HttpRequestMessage CredentialBearingPost(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(SecretBody, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-in-authorization-header");
        request.Headers.TryAddWithoutValidation("X-Api-Key", SecretHeader);
        return request;
    }

    // ── Runtime behaviour of .NET redirects on THIS runtime ───────────────────
    // These use a bare SocketsHttpHandler (framework defaults) on purpose: they document what the
    // guarded handler used to inherit, and they are the reason the fix is a refusal rather than a
    // header filter. If a future runtime changes any of this, these fail and say so.

    [Fact]
    public async Task Runtime_A307_ReplaysTheBodyAndCustomHeaderToTheRedirectTarget()
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        using var supplier = StubHttpServer.Start(StubHttpServer.Redirect(307, $"{attacker.LocalhostUrl}/collect"));

        using var handler = new System.Net.Http.SocketsHttpHandler();   // AllowAutoRedirect defaults to true
        using var client = new HttpClient(handler);

        var response = await client.SendAsync(CredentialBearingPost($"{supplier.BaseUrl}/deliver"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the runtime followed the redirect");
        attacker.Received.Should().HaveCount(1);

        var leaked = attacker.Received[0];
        leaked.Method.Should().Be("POST", "307 preserves the method");
        leaked.BodyText.Should().Be(SecretBody,
            "307 replays the body verbatim — this is the ERP password crossing to a second origin");
        leaked.Headers.Should().ContainKey("X-Api-Key");
        leaked.Headers["X-Api-Key"].Should().Be(SecretHeader,
            "the runtime never strips a custom header, so an apikey credential crosses too");
    }

    [Fact]
    public async Task Runtime_DropsAuthorizationOnlyWhenTheHostNameDiffers()
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        // Redirect from 127.0.0.1 to the literal name "localhost" — same socket, different host
        // STRING, which is what the runtime's same-host comparison actually looks at.
        using var supplier = StubHttpServer.Start(StubHttpServer.Redirect(307, $"{attacker.LocalhostUrl}/collect"));

        using var handler = new System.Net.Http.SocketsHttpHandler();
        using var client = new HttpClient(handler);

        await client.SendAsync(CredentialBearingPost($"{supplier.BaseUrl}/deliver"));

        attacker.Received.Should().HaveCount(1);
        attacker.Received[0].Headers.Should().NotContainKey("Authorization",
            "the runtime drops Authorization across a host change — and ONLY Authorization");
    }

    [Fact]
    public async Task Runtime_A302DowngradesToGetAndDropsTheBodyButNotTheCustomHeader()
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        using var supplier = StubHttpServer.Start(StubHttpServer.Redirect(302, $"{attacker.LocalhostUrl}/collect"));

        using var handler = new System.Net.Http.SocketsHttpHandler();
        using var client = new HttpClient(handler);

        await client.SendAsync(CredentialBearingPost($"{supplier.BaseUrl}/deliver"));

        attacker.Received.Should().HaveCount(1);
        var leaked = attacker.Received[0];
        leaked.Method.Should().Be("GET", "302 from a POST is downgraded to GET");
        leaked.Body.Should().BeEmpty("the method change drops the body — 307/308 are the body-replay cases");
        leaked.Headers.Should().ContainKey("X-Api-Key",
            "a custom-header credential still crosses even when the body does not");
    }

    // ── The fix: the guarded transport refuses ────────────────────────────────

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task GuardedHandler_DoesNotFollowARedirect_SoTheSecondOriginReceivesNothing(int status)
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        using var supplier = StubHttpServer.Start(StubHttpServer.Redirect(status, $"{attacker.LocalhostUrl}/collect"));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler();
        using var client = new HttpClient(handler);

        var response = await client.SendAsync(CredentialBearingPost($"{supplier.BaseUrl}/deliver"));

        attacker.Received.Should().BeEmpty(
            "nothing — not the body, not the custom header, not even a bare request — may reach a " +
            "host the tenant never configured and ValidateAsync never saw");
        ((int)response.StatusCode).Should().Be(status,
            "the redirect is surfaced to the caller as the response it is, not followed");
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task GuardedHandler_StillDeliversToAnEndpointThatDoesNotRedirect()
    {
        using var supplier = StubHttpServer.Start(StubHttpServer.Ok("ACCEPTED"));

        using var handler = LoopbackGuard().CreateGuardedHttpHandler();
        using var client = new HttpClient(handler);

        var response = await client.SendAsync(CredentialBearingPost($"{supplier.BaseUrl}/deliver"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ACCEPTED");
        supplier.Received.Should().HaveCount(1);
        supplier.Received[0].BodyText.Should().Be(SecretBody,
            "a legitimate endpoint must still receive exactly what it always did");
    }

    // ── Channel-level proof: HTTP delivery ────────────────────────────────────

    [Fact]
    public async Task HttpDelivery_AgainstARedirectingSupplier_NeverReachesTheRedirectTarget()
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        using var supplier = StubHttpServer.Start(StubHttpServer.Redirect(307, $"{attacker.LocalhostUrl}/collect"));

        var result = await DispatchHttpAsync($"{supplier.BaseUrl}/deliver");

        attacker.Received.Should().BeEmpty(
            "the purchase order and the apikey credential must not cross to a second origin");
        result.Success.Should().BeFalse("a 3xx is not a delivery acknowledgement");

        // A refusal the operator cannot act on is a different kind of failure. The message must
        // name what happened and what to change, not report an opaque status code.
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("redirected");
        result.ErrorMessage.Should().Contain("Update the delivery URL",
            "the operator has to be told which setting resolves this");
    }

    [Fact]
    public async Task HttpDelivery_AgainstAPlainSupplierEndpoint_StillSucceeds()
    {
        using var supplier = StubHttpServer.Start(StubHttpServer.Ok("ACCEPTED"));

        var result = await DispatchHttpAsync($"{supplier.BaseUrl}/deliver");

        result.Success.Should().BeTrue("the ordinary happy path must be untouched");
        supplier.Received.Should().HaveCount(1);
        supplier.Received[0].Headers.Should().ContainKey("X-Api-Key");
    }

    private static async Task<ProcuLink.Core.Services.Delivery.DeliveryResult> DispatchHttpAsync(string url)
    {
        var guard = LoopbackGuard();
        var services = new ServiceCollection();
        services.AddHttpClient("delivery", c => c.Timeout = TimeSpan.FromSeconds(15));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var dispatcher = new HttpDeliveryDispatcher(factory, guard, NullLogger<HttpDeliveryDispatcher>.Instance);

        return await dispatcher.DispatchAsync(
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
                ConfigJson = JsonSerializer.Serialize(new { url, method = "POST", timeoutSeconds = 15 }),
                EncryptedCredentials = string.Empty,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            JsonSerializer.Serialize(new { type = "apikey", header = "X-Api-Key", value = SecretHeader }),
            default);
    }

    // ── Channel-level proof: Directo, whose credentials ARE the body ──────────

    [Fact]
    public async Task Directo_AgainstARedirectingEndpoint_NeverReplaysThePasswordToTheRedirectTarget()
    {
        using var attacker = StubHttpServer.Start(StubHttpServer.Ok());
        using var erp = StubHttpServer.Start(StubHttpServer.Redirect(307, $"{attacker.LocalhostUrl}/collect"));

        var result = await SendDirectoAsync($"{erp.BaseUrl}/xmlcore");

        attacker.Received.Should().BeEmpty(
            "Directo puts user/password in the form body, which a 307 replays verbatim");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Directo_AgainstAPlainEndpoint_StillSucceeds()
    {
        using var erp = StubHttpServer.Start(StubHttpServer.Ok("<result status=\"ok\"/>"));

        var result = await SendDirectoAsync($"{erp.BaseUrl}/xmlcore");

        result.Success.Should().BeTrue();
        erp.Received.Should().HaveCount(1);
        erp.Received[0].BodyText.Should().Contain("password=hunter2",
            "the legitimate endpoint the tenant configured still gets the credentials it needs");
    }

    private static async Task<ProcuLink.Core.Services.Erp.ErpDeliveryResult> SendDirectoAsync(string url)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton(new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance));
        // EXACTLY the API/Worker Program.cs wiring.
        services.AddHttpClient("delivery", c => c.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    sp.GetRequiredService<OutboundRequestGuard>().CreateGuardedHttpHandler());

        var connector = new DirectoConnector(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            NullLogger<DirectoConnector>.Instance);

        return await connector.SendAsync(
            new ProcuLink.Core.Services.Erp.ErpDeliveryRequest(
                "<order id=\"PO-1\" />"u8.ToArray(),
                "PO-1.xml",
                "application/xml",
                new SupplierDeliveryConfig
                {
                    Id = Guid.NewGuid(),
                    OrgId = Guid.NewGuid(),
                    SupplierId = Guid.NewGuid(),
                    Protocol = DeliveryProtocolConstants.ErpDirecto,
                    AutoDeliver = true,
                    ConfigJson = JsonSerializer.Serialize(new { url, database = "acme", timeoutSeconds = 15 }),
                    EncryptedCredentials = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                },
                JsonSerializer.Serialize(new { user = "apiuser", password = "hunter2" })),
            default);
    }
}
