using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-22 — the controller must hand Postmark's <c>MessageID</c> to the router.
///
/// <para><b>Why this file exists as a separate test rather than more router cases.</b> The dedupe
/// suite (<c>IngestDedupePostgresTests</c>) proves the ROUTER honours a provider message id: same id
/// deduplicates, different ids do not, and the stored ledger key is namespaced. Every one of those
/// cells constructs its own <see cref="InboundEmailPayload"/>, so none of them can see whether the
/// CONTROLLER ever populates the field.</para>
///
/// <para>That gap was measured, not assumed. A mutation branch deleted
/// <c>ProviderMessageId: body.MessageID</c> from <see cref="InboundEmailController"/> and CI came
/// back <b>green</b> — the whole dedupe suite included. <c>ProviderMessageId</c> is an optional
/// parameter, so the deletion compiles; and with it gone <c>ClaimKeyFor</c> returns the bare
/// <c>"postmark:"</c> prefix and every org gets ONE claim bucket keyed only on the attachment hash.
/// Two different emails carrying byte-identical attachments then collide, and the second is answered
/// 200 with an empty <c>CreatedOrderIds</c>: a silently lost purchase order.</para>
///
/// <para>The lesson is general enough to state: a test that builds the DTO itself can never prove
/// who fills it in. The wiring needs its own assertion at the seam that does the wiring.</para>
/// </summary>
public class InboundEmailControllerMessageIdTests
{
    private const string Secret = "pm-inbound-secret-123";
    private const string Recipient = "acme@orders.proculink.eu";

    [Fact]
    public async Task Postmark_PassesTheProviderMessageId_ToTheRouter()
    {
        var (controller, router) = BuildController();

        await controller.Postmark(BodyWith("abc-123-message-id"), CancellationToken.None);

        Assert.NotNull(router.LastPayload);
        Assert.Equal("abc-123-message-id", router.LastPayload!.ProviderMessageId);
    }

    [Fact]
    public async Task Postmark_PassesTheMessageIdVerbatim_SoTheRouterOwnsTheNamespacing()
    {
        // A raw RFC-822 Message-Id arrives with angle brackets. The controller must not trim,
        // lower-case or prefix it: `InboundEmailRouter.ClaimKeyFor` adds the `postmark:` namespace,
        // and a second normalisation here would mean the same message hashes to two different
        // ledger keys depending on which layer saw it first.
        var (controller, router) = BuildController();

        await controller.Postmark(BodyWith("<CAF=abc@mail.example.com>"), CancellationToken.None);

        Assert.Equal("<CAF=abc@mail.example.com>", router.LastPayload!.ProviderMessageId);
    }

    [Fact]
    public async Task Postmark_WithNoMessageId_PassesNull_RatherThanInventingOne()
    {
        // A provider that omits the id must reach the router as null so the router can apply its
        // documented content-hash fallback. Substituting an empty string or a generated value here
        // would silently defeat that fallback.
        var (controller, router) = BuildController();

        await controller.Postmark(BodyWith(null), CancellationToken.None);

        Assert.Null(router.LastPayload!.ProviderMessageId);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static (InboundEmailController Controller, CapturingRouter Router) BuildController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Inbound:Postmark:WebhookToken"] = Secret,
            })
            .Build();

        var router = new CapturingRouter();
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues> { ["token"] = Secret });

        var controller = new InboundEmailController(router, config, NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (controller, router);
    }

    private static InboundEmailController.PostmarkInboundPayload BodyWith(string? messageId) => new()
    {
        From = "buyer@example.com",
        To = Recipient,
        Subject = "PO #1",
        MessageID = messageId,
        Attachments = new List<InboundEmailController.PostmarkInboundAttachment>(),
    };

    /// <summary>Keeps the payload the controller built, which is the whole point of this file.</summary>
    private sealed class CapturingRouter : IInboundEmailRouter
    {
        public InboundEmailPayload? LastPayload { get; private set; }

        public Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
        {
            LastPayload = payload;
            return Task.FromResult(new InboundEmailResult(
                Success: true,
                OrgId: Guid.NewGuid(),
                CreatedOrderIds: new[] { Guid.NewGuid() },
                Error: null));
        }
    }
}
