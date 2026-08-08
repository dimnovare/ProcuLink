using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Email;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Pins what the Postmark inbound webhook answers when a message is not ingested —
/// which is the same thing as pinning how many more times Postmark will deliver it.
///
/// Measured on production 2026-07-24: a 422 does NOT stop Postmark. One message
/// produced three webhook attempts in six minutes and stayed <c>Scheduled</c>.
/// Postmark's documented policy is ten retries over roughly 10.5 hours for any
/// non-200, after which the message is filed as <c>Failed</c> and can still be
/// re-fired by hand; a 200 marks it <c>Processed</c> and it is finished for good.
///
/// So the status is a routing decision, not decoration: 200 for a message no
/// re-delivery could ever help, non-200 for one that only failed because our side
/// was unavailable.
/// </summary>
public class InboundEmailControllerRetryContractTests
{
    private const string Secret = "pm-inbound-secret-123";
    private const string Recipient = "acme@orders.proculink.eu";

    [Fact]
    public async Task PermanentRejection_Returns200Ignored_SoPostmarkStopsRetrying()
    {
        var (controller, _) = BuildController(new InboundEmailResult(
            Success: false,
            OrgId: null,
            CreatedOrderIds: Array.Empty<Guid>(),
            Error: "Recipient 'someone@mail.example' does not look like an inbound ProcuLink address.",
            RejectionKind: InboundEmailRejectionKind.Permanent));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ignored", Prop(ok.Value, "status"));
        Assert.Contains("does not look like", Prop(ok.Value, "reason") ?? string.Empty);
    }

    [Fact]
    public async Task TransientRejection_Returns422_SoPostmarkKeepsRetrying()
    {
        var orgId = Guid.NewGuid();
        var (controller, _) = BuildController(new InboundEmailResult(
            Success: false,
            OrgId: orgId,
            CreatedOrderIds: Array.Empty<Guid>(),
            Error: "Organisation is in 'read_only' status and cannot ingest new orders.",
            RejectionKind: InboundEmailRejectionKind.Transient));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(orgId.ToString(), Prop(unprocessable.Value, "orgId"));
    }

    [Fact]
    public async Task UnlabelledRejection_KeepsItsRetries_SoAnUnthoughtBranchCannotDropAnOrder()
    {
        // A future reject branch that forgets to classify itself must cost duplicate
        // webhook calls, never a silently discarded purchase order.
        var (controller, _) = BuildController(new InboundEmailResult(
            Success: false,
            OrgId: Guid.NewGuid(),
            CreatedOrderIds: Array.Empty<Guid>(),
            Error: "something new went wrong"));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task MissingRecipient_Returns200Ignored_AndNeverCallsRouter()
    {
        var (controller, router) = BuildController(SuccessResult());

        var result = await controller.Postmark(
            new InboundEmailController.PostmarkInboundPayload { From = "buyer@example.com", Subject = "PO" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ignored", Prop(ok.Value, "status"));
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task EmptyBody_Returns200Ignored_BecauseTheSameNullArrivesEveryRetry()
    {
        var (controller, router) = BuildController(SuccessResult());

        var result = await controller.Postmark(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ignored", Prop(ok.Value, "status"));
        Assert.Equal(0, router.Calls);
    }

    [Fact]
    public async Task IngestedMessage_Returns200_WithoutTheIgnoredMarker()
    {
        // Both outcomes are 200s, so the body is what tells an operator reading
        // Postmark's activity log whether the mail actually became an order.
        var orderId = Guid.NewGuid();
        var (controller, _) = BuildController(new InboundEmailResult(
            Success: true,
            OrgId: Guid.NewGuid(),
            CreatedOrderIds: new[] { orderId },
            Error: null));

        var result = await controller.Postmark(ValidBody(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Null(Prop(ok.Value, "status"));
        Assert.NotNull(ok.Value?.GetType().GetProperty("createdOrderIds"));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static (InboundEmailController Controller, StubRouter Router) BuildController(InboundEmailResult result)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Inbound:Postmark:WebhookToken"] = Secret,
            })
            .Build();

        var router = new StubRouter(result);
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues> { ["token"] = Secret });

        var controller = new InboundEmailController(router, config, NullLogger<InboundEmailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (controller, router);
    }

    private static InboundEmailResult SuccessResult() =>
        new(Success: true, OrgId: Guid.NewGuid(), CreatedOrderIds: Array.Empty<Guid>(), Error: null);

    private static InboundEmailController.PostmarkInboundPayload ValidBody() => new()
    {
        From = "buyer@example.com",
        To = Recipient,
        Subject = "PO #1",
        Attachments = new List<InboundEmailController.PostmarkInboundAttachment>(),
    };

    /// <summary>Reads a property off the controller's anonymous response body.</summary>
    private static string? Prop(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value)?.ToString();

    private sealed class StubRouter : IInboundEmailRouter
    {
        private readonly InboundEmailResult _result;

        public StubRouter(InboundEmailResult result) => _result = result;

        public int Calls { get; private set; }

        public Task<InboundEmailResult> RouteAsync(InboundEmailPayload payload, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
