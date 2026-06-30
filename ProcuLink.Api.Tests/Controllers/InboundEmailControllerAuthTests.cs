using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Webhook-auth coverage for <see cref="InboundEmailController"/>.
///
/// Postmark Inbound does NOT send a custom auth header — it only supports
/// credentials baked into the webhook URL (Basic Auth or a query token). The
/// controller therefore accepts the shared secret from the <c>?token=</c> query,
/// the HTTP Basic-Auth password, or the legacy <c>X-Postmark-Server-Token</c>
/// header. These tests pin all three accept paths plus the reject paths so the
/// real-Postmark contract can't silently regress to header-only again.
/// </summary>
public class InboundEmailControllerAuthTests
{
    private const string Secret = "pm-inbound-secret-123";
    private const string Recipient = "acme@orders.proculink.eu";

    // ── Accept paths ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryToken_Valid_Returns200()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Query =
            new QueryCollection(new Dictionary<string, StringValues> { ["token"] = Secret });

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public async Task BasicAuthPassword_Valid_Returns200()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Headers.Authorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"postmark:{Secret}"));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public async Task BasicAuthNoColon_TreatsWholeValueAsToken_Returns200()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        // No "user:" — the entire decoded credential is the token.
        controller.HttpContext.Request.Headers.Authorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public async Task LegacyHeader_Valid_Returns200()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Headers["X-Postmark-Server-Token"] = Secret;

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, router.Calls);
    }

    // ── Reject paths ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCredential_Returns401_AndNeverCallsRouter()
    {
        var (controller, router) = BuildController(configuredToken: Secret);

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task EmptyQueryToken_Returns401_AndNeverCallsRouter()
    {
        // ?token= with no value must NOT authenticate — it falls through to the
        // other credential sources, finds none, and the constant-time gate fails.
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Query =
            new QueryCollection(new Dictionary<string, StringValues> { ["token"] = string.Empty });

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task WrongQueryToken_Returns401_AndNeverCallsRouter()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Query =
            new QueryCollection(new Dictionary<string, StringValues> { ["token"] = "not-the-secret" });

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task WrongBasicAuthPassword_Returns401()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        controller.HttpContext.Request.Headers.Authorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("postmark:wrong"));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task MalformedBasicAuth_FallsThroughToHeader_Returns200()
    {
        var (controller, router) = BuildController(configuredToken: Secret);
        // Not valid base64 → Basic parse fails → falls through to the header.
        controller.HttpContext.Request.Headers.Authorization = "Basic !!!not-base64!!!";
        controller.HttpContext.Request.Headers["X-Postmark-Server-Token"] = Secret;

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, router.Calls);
    }

    [Fact]
    public async Task TokenNotConfigured_Returns401_EvenWithCredentialPresented()
    {
        // Operator hasn't set Inbound:Postmark:WebhookToken — refuse all inbound.
        var (controller, router) = BuildController(configuredToken: null);
        controller.HttpContext.Request.Query =
            new QueryCollection(new Dictionary<string, StringValues> { ["token"] = "anything" });

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, router.Calls);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static (InboundEmailController Controller, RecordingRouter Router) BuildController(string? configuredToken)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredToken is not null)
            settings["Inbound:Postmark:WebhookToken"] = configuredToken;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var router = new RecordingRouter();

        var controller = new InboundEmailController(router, config, NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return (controller, router);
    }

    private static InboundEmailController.PostmarkInboundPayload ValidBody() => new()
    {
        From = "buyer@example.com",
        To = Recipient,
        Subject = "PO #1",
        Attachments = new List<InboundEmailController.PostmarkInboundAttachment>(),
    };

    /// <summary>
    /// Records how many times the router was invoked and always reports success.
    /// Lets the auth tests assert that a rejected request never reaches routing.
    /// </summary>
    private sealed class RecordingRouter : IInboundEmailRouter
    {
        public int Calls { get; private set; }

        public Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new InboundEmailResult(
                Success: true,
                OrgId: Guid.NewGuid(),
                CreatedOrderIds: new[] { Guid.NewGuid() },
                Error: null));
        }
    }
}
