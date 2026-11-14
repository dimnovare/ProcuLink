using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Pins what the controller hands the router for an attachment body: corrupt base64 reaches it as
/// <c>Undecodable</c> (carrying none of the offending bytes), an absent or blank body as
/// <c>Decoded</c>, and valid base64 as the sender's bytes. A corrupt body used to be
/// indistinguishable from an absent one, so the skip left no trace.
/// </summary>
public class InboundEmailAttachmentDecodeTests
{
    private const string WebhookToken = "pm-inbound-secret-123";
    private const string Recipient = "orders@acme.proculink.eu";

    /// <summary>Not valid base64 under any padding: '!' is outside the alphabet.</summary>
    private const string CorruptBase64 = "!!!this-is-not-base64!!!";


    // ── The regression ───────────────────────────────────────────────────────

    // ── The two causes are told apart, in both directions ────────────────────

    // ── The undecodable bytes stop at the decode site ────────────────────────

    [Fact]
    public async Task CorruptBase64_ReachesTheRouterAsUndecodable_CarryingNoneOfTheOffendingBytes()
    {
        // The router half is asserted above through a real database. This pins the OTHER half of
        // the seam directly: what the controller hands over. The offending string is a customer's
        // file content, so it must not travel past the decoder in any form.
        var captured = new CapturingRouter();
        var controller = BuildController(captured);

        await controller.Postmark(BodyWith(CorruptBase64), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Undecodable);
        attachment.Content.Should().BeEmpty("there is nothing to carry, and the raw base64 must not travel");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AbsentOrBlankContent_ReachesTheRouterAsDecoded_NotAsAFailure(string? content)
    {
        var captured = new CapturingRouter();
        var controller = BuildController(captured);

        await controller.Postmark(BodyWith(content), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Decoded,
            "an absent body decodes cleanly to nothing — it is an empty attachment, not a decode failure");
        attachment.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidBase64_ReachesTheRouterDecoded_WithTheSendersBytes()
    {
        var captured = new CapturingRouter();
        var controller = BuildController(captured);
        var bytes = "po,qty\r\nPO-1,5\r\n"u8.ToArray();

        await controller.Postmark(BodyWith(Convert.ToBase64String(bytes)), CancellationToken.None);

        var attachment = captured.Payload!.Attachments.Should().ContainSingle().Subject;
        attachment.Decode.Should().Be(InboundAttachmentDecode.Decoded);
        attachment.Content.Should().Equal(bytes);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static InboundEmailController.PostmarkInboundPayload BodyWith(string? attachmentContent) => new()
    {
        From = "buyer@example.com",
        To = Recipient,
        Subject = "PO #12345",
        MessageID = Guid.NewGuid().ToString(),
        Attachments = new List<InboundEmailController.PostmarkInboundAttachment>
        {
            new() { Name = "po.csv", ContentType = "text/csv", Content = attachmentContent },
        },
    };

    /// <summary>Controller wired to a capturing stub, for asserting what the controller hands over.</summary>
    private static InboundEmailController BuildController(IInboundEmailRouter router) =>
        Build(router, Configuration());

    private static InboundEmailController Build(IInboundEmailRouter router, IConfiguration config)
    {
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { ["token"] = WebhookToken });

        return new InboundEmailController(router, config, NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static IConfiguration Configuration() =>
        InboundAddressTestHarness.Configuration(new Dictionary<string, string?>
        {
            ["Inbound:Postmark:WebhookToken"] = WebhookToken,
        });

    // ── Doubles ─────────────────────────────────────────────────────────────

    private sealed class CapturingRouter : IInboundEmailRouter
    {
        public InboundEmailPayload? Payload { get; private set; }

        public Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
        {
            Payload = payload;
            return Task.FromResult(new InboundEmailResult(
                Success: true, OrgId: Guid.NewGuid(), CreatedOrderIds: Array.Empty<Guid>(), Error: null));
        }
    }

}
