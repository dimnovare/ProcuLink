using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// B-12, webhook half — the endpoint that turns Postmark's asynchronous failure reports into
/// something the handler can act on.
///
/// <para>What is under test here is the CLASSIFICATION and the CORRELATION READ, because those are
/// the two places this can be wrong while looking right: acting on a soft bounce manufactures a
/// failure the supplier never saw, and reading the metadata key case-sensitively finds nothing on
/// every real bounce while every hand-written test payload matches.</para>
/// </summary>
public class PostmarkBounceWebhookTests
{
    private const string Token = "webhook-token-b12";

    private static IConfiguration Config(string? token = Token, string? proxySecret = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Inbound:Postmark:WebhookToken"] = token,
            ["Inbound:Postmark:ProxySecret"] = proxySecret,
        }).Build();

    private static (InboundEmailController Controller, Mock<IDeliveryBounceHandler> Handler) Build(
        IConfiguration? config = null, string? presentedToken = Token)
    {
        var http = new DefaultHttpContext();
        if (presentedToken is not null)
            http.Request.Headers["X-Postmark-Server-Token"] = presentedToken;

        var controller = new InboundEmailController(
            new Mock<IInboundEmailRouter>().Object,
            config ?? Config(),
            NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var handler = new Mock<IDeliveryBounceHandler>();
        handler.Setup(h => h.HandleAsync(It.IsAny<DeliveryBounceNotification>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new DeliveryBounceResult(DeliveryBounceOutcome.OrderMarkedFailed, Guid.NewGuid()));

        return (controller, handler);
    }

    private static InboundEmailController.PostmarkBouncePayload HardBouncePayload(
        Dictionary<string, string>? metadata = null) => new()
    {
        RecordType = "Bounce",
        Type = "HardBounce",
        TypeCode = 1,
        MessageID = "pm-1",
        Email = "suplier@example.com",
        Description = "The server was unable to deliver your message.",
        Inactive = true,
        Metadata = metadata ?? new Dictionary<string, string>
        {
            [DeliveryBounceMetadata.IdempotencyKeyField] = "delivery-key-1",
        },
    };

    // ── The correlation read ────────────────────────────────────────────────

    /// <summary>
    /// Postmark LOWER-CASES metadata keys on the way back out. A case-sensitive read finds nothing
    /// on every real bounce, while every test that spells the constant exactly still passes — which
    /// is precisely the shape that ships broken.
    /// </summary>
    [Theory]
    [InlineData("pl_delivery_key")]
    [InlineData("PL_DELIVERY_KEY")]
    [InlineData("Pl_Delivery_Key")]
    public async Task Bounce_ReadsTheDeliveryKey_WhateverCaseTheProviderEchoesItIn(string key)
    {
        var (controller, handler) = Build();

        await controller.PostmarkBounce(
            HardBouncePayload(new Dictionary<string, string> { [key] = "delivery-key-1" }),
            handler.Object, CancellationToken.None);

        handler.Verify(h => h.HandleAsync(
            It.Is<DeliveryBounceNotification>(n => n.IdempotencyKey == "delivery-key-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Bounce_WithNoMetadata_StillReachesTheHandler_WithANullKey()
    {
        // The handler is what reports an unattributable bounce. Swallowing it here instead would
        // make the failure invisible in exactly the case that matters.
        var (controller, handler) = Build();

        await controller.PostmarkBounce(
            HardBouncePayload(new Dictionary<string, string>()), handler.Object, CancellationToken.None);

        handler.Verify(h => h.HandleAsync(
            It.Is<DeliveryBounceNotification>(n => n.IdempotencyKey == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Classification ──────────────────────────────────────────────────────

    [Fact]
    public async Task HardBounce_IsClassifiedTerminal_AndReachesTheHandler()
    {
        var (controller, handler) = Build();

        var result = await controller.PostmarkBounce(HardBouncePayload(), handler.Object, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        handler.Verify(h => h.HandleAsync(
            It.Is<DeliveryBounceNotification>(n => n.Kind == DeliveryBounceKind.Hard),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("SpamComplaint", null)]
    [InlineData("Bounce", "SpamNotification")]
    public async Task SpamComplaint_IsClassifiedTerminal_InBothShapesPostmarkSendsIt(
        string recordType, string? type)
    {
        var (controller, handler) = Build();

        var payload = HardBouncePayload();
        payload.RecordType = recordType;
        payload.Type = type;

        await controller.PostmarkBounce(payload, handler.Object, CancellationToken.None);

        handler.Verify(h => h.HandleAsync(
            It.Is<DeliveryBounceNotification>(n => n.Kind == DeliveryBounceKind.SpamComplaint),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Postmark retries soft bounces itself, and <c>Inactive</c> is the CONSEQUENCE it applies after
    /// a hard bounce — never a cause on its own. Every payload below carries <c>Inactive = true</c>
    /// on purpose: a classifier that keyed off it would fail orders that were never undeliverable.
    /// </summary>
    [Theory]
    [InlineData("SoftBounce")]
    [InlineData("Transient")]
    [InlineData("DnsError")]
    [InlineData("Blocked")]
    [InlineData("SubscriptionChange")]
    public async Task NonTerminalBounceTypes_AreIgnored_AndNeverReachTheHandler(string type)
    {
        var (controller, handler) = Build();

        var payload = HardBouncePayload();
        payload.Type = type;
        payload.Inactive = true;

        var result = await controller.PostmarkBounce(payload, handler.Object, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "200 ends Postmark's retries; a soft bounce it is already retrying itself is not our problem");
        handler.Verify(h => h.HandleAsync(
            It.IsAny<DeliveryBounceNotification>(), It.IsAny<CancellationToken>()), Times.Never,
            "moving an order off 'delivered' for a soft bounce manufactures a failure the supplier never saw");
    }

    // ── Authentication ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("wrong-token")]
    [InlineData(null)]
    public async Task BadOrMissingToken_Is401_AndNeverReachesTheHandler(string? presented)
    {
        var (controller, handler) = Build(presentedToken: presented);

        var result = await controller.PostmarkBounce(HardBouncePayload(), handler.Object, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        handler.Verify(h => h.HandleAsync(
            It.IsAny<DeliveryBounceNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnconfiguredToken_Is401_RatherThanAcceptingAnythingThatArrives()
    {
        var (controller, handler) = Build(config: Config(token: null));

        var result = await controller.PostmarkBounce(HardBouncePayload(), handler.Object, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        handler.Verify(h => h.HandleAsync(
            It.IsAny<DeliveryBounceNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProxySecret_WhenSet_MustAlsoBePresented()
    {
        var (controller, handler) = Build(config: Config(proxySecret: "edge-secret"));

        var refused = await controller.PostmarkBounce(HardBouncePayload(), handler.Object, CancellationToken.None);
        refused.Should().BeOfType<UnauthorizedObjectResult>();

        controller.HttpContext.Request.Headers["X-Inbound-Proxy-Secret"] = "edge-secret";
        var accepted = await controller.PostmarkBounce(HardBouncePayload(), handler.Object, CancellationToken.None);
        accepted.Should().BeOfType<OkObjectResult>(
            "the anti-vacuity half — the refusal above must be caused by the secret, not by the harness");
    }
}
