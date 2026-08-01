using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-39 §4.4 — an unescaped HTML document was spliced into operator-facing copy.
///
/// <para>The message shown after a live 404, verbatim from <c>GET /api/orders/{id}</c>:</para>
///
/// <code>
/// The supplier's endpoint was not found (HTTP 404). The delivery address in this supplier's
/// delivery settings has most likely moved or contains a typo — confirm the address with the
/// supplier, correct it there, then send the order again. The supplier's endpoint said:
/// &lt;!-- Tip: Set the Accept header to application/json to get errors in JSON format. --&gt;
/// &lt;!DOCTYPE html&gt; &lt;html&gt; &lt;head&gt;     &lt;title&gt;Error: Token &amp;quot;9a1f85b7-…&amp;quot; not found -
/// </code>
///
/// <para>The first sentence is good copy. Everything after "said:" is a web server's error page,
/// cut off mid-tag by the 200-character cap. The cap was doing its job; nothing was asking whether
/// the bytes were a message in the first place.</para>
///
/// <para>Same class of defect as G6, and the frontend already settled the principle one function
/// over in <c>src/lib/api-client.ts</c>: <c>parseApiErrorBody</c> extracts a message it can
/// identify and returns nothing at all otherwise — it never falls back to pasting the raw body in
/// front of a person. These tests hold the delivery path to the same rule, at both places that
/// quote a remote body.</para>
/// </summary>
public class SupplierResponseBodyQuotingTests
{
    /// <summary>The body webhook.site actually returned on the expired bin, shortened in the middle only.</summary>
    private const string ProductionHtmlBody =
        "<!-- Tip: Set the Accept header to application/json to get errors in JSON format. -->\n"
      + "<!DOCTYPE html>\n<html>\n<head>\n    <title>Error: Token \"9a1f85b7-0000-0000-0000-000000000000\" not found - Webhook.site</title>\n"
      + "</head>\n<body>\n<h1>Token not found</h1>\n</body>\n</html>";

    // ── DescribeFailure: the sentence the operator reads ──────────────────────

    [Fact]
    public void HtmlErrorPage_IsNamed_NotQuoted()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 404, supplierReason: ProductionHtmlBody,
            supplierReasonObservable: true, dispatcherMessage: "HTTP 404.");

        message.Should().NotBeNull();
        message.Should().NotContain("<");
        message.Should().NotContain("DOCTYPE");
        message.Should().NotContain("Set the Accept header");
        message.Should().NotContain("The supplier's endpoint said:");
        message.Should().Contain("HTML error page");
        // The part that was always good copy survives untouched.
        message.Should().Contain("not found (HTTP 404)");
    }

    [Fact]
    public void XmlBody_IsNamed_NotQuoted()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 404, supplierReason: "<?xml version=\"1.0\"?><Error><Code>NoSuchKey</Code></Error>",
            supplierReasonObservable: true, dispatcherMessage: null);

        message.Should().NotContain("<");
        message.Should().Contain("XML response");
    }

    [Fact]
    public void PlainTextReason_IsStillQuoted_Verbatim()
    {
        // The whole reason this passthrough exists. A supplier that answers in words gets
        // those words in front of the operator, unchanged.
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 401, supplierReason: "token expired at 2026-07-30T09:00Z",
            supplierReasonObservable: true, dispatcherMessage: null);

        message.Should().Contain("The supplier's endpoint said: token expired at 2026-07-30T09:00Z");
    }

    [Fact]
    public void JsonBody_IsMinedForItsMessage_NotPastedWhole()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 401,
            supplierReason: """{"error":"invalid_token","message":"The access token expired.","traceId":"abc-123"}""",
            supplierReasonObservable: true, dispatcherMessage: null);

        message.Should().Contain("The access token expired.");
        message.Should().NotContain("traceId");
        message.Should().NotContain("{");
    }

    [Fact]
    public void JsonBodyWithNoMessageField_IsNamed_NotQuoted()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 401, supplierReason: """{"status":401,"traceId":"abc-123"}""",
            supplierReasonObservable: true, dispatcherMessage: null);

        message.Should().NotContain("traceId");
        message.Should().NotContain("{");
        message.Should().Contain("JSON error");
    }

    [Fact]
    public void LongPlainTextReason_IsStillCapped()
    {
        var wall = new string('x', 5_000);

        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 401, supplierReason: wall,
            supplierReasonObservable: true, dispatcherMessage: null);

        message!.Length.Should().BeLessThan(600);
    }

    [Fact]
    public void NoReason_LeavesTheHintAlone()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            responseCode: 404, supplierReason: "   ",
            supplierReasonObservable: true, dispatcherMessage: null);

        message.Should().NotContain("The supplier's endpoint said:");
        message.Should().NotContain("HTML");
    }

    // ── The second passthrough: the dispatcher's own summary ──────────────────
    //
    // DescribeFailure returns `dispatcherMessage` UNCHANGED whenever it has no hint for the
    // response code. So a code the table does not name (a bare 400, a 500) reaches the operator
    // through the dispatcher's sentence instead — which pasted its own 120-char slice of the
    // same body. Fixing only DescribeFailure would have left that door open.

    // These three drive the REAL dispatcher through a fake transport rather than calling the
    // shared helper. They were written the lazy way first — asserting on
    // SupplierResponseClassification directly — and a mutation that reverted
    // HttpDeliveryDispatcher.BuildFailureMessage to its old 120-character slice left all of them
    // green. A test that names a component it never executes is worse than no test: it reports
    // the second passthrough as covered while leaving it open.

    private static HttpDeliveryDispatcher DispatcherAnswering(HttpStatusCode code, string body)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
            .Build();
        var guard   = new OutboundRequestGuard(configuration, NullLogger<OutboundRequestGuard>.Instance);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("delivery")).Returns(() => new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "OK")));

        return new TestableHttpDeliveryDispatcher(
            factory.Object, guard, new HttpClient(new FakeHttpMessageHandler(code, body)));
    }

    private static async Task<DeliveryResult> DeliverAgainst(HttpStatusCode code, string body)
    {
        var config = new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = Guid.NewGuid(),
            SupplierId           = Guid.NewGuid(),
            Protocol             = "http",
            AutoDeliver          = false,
            ConfigJson           = JsonSerializer.Serialize(new { url = "https://supplier.example/orders", method = "POST", timeoutSeconds = 30 }),
            EncryptedCredentials = string.Empty,
        };

        return await DispatcherAnswering(code, body).DispatchAsync(
            Encoding.UTF8.GetBytes("PO,DATE\r\n001,2026-08-01"),
            "order.csv", "text/csv", config,
            JsonSerializer.Serialize(new { type = "none" }), default);
    }

    [Fact]
    public async Task Dispatcher_NamesAnHtmlPage_RatherThanSlicingIt()
    {
        // A bare 400 is the case that matters here: SupplierResponseClassification has no named
        // hint for it, so DescribeFailure passes THIS message through to the operator unchanged.
        var result = await DeliverAgainst(HttpStatusCode.BadRequest, ProductionHtmlBody);

        result.ErrorMessage.Should().NotContain("<");
        result.ErrorMessage.Should().NotContain("DOCTYPE");
        result.ErrorMessage.Should().Contain("HTML error page");
        // The body itself still travels verbatim for the audit trail — only the sentence changed.
        result.ResponseBody.Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task Dispatcher_KeepsWordsThatAreWords()
    {
        var result = await DeliverAgainst(HttpStatusCode.BadRequest, "Order rejected: unknown item code ACM-99.");

        result.ErrorMessage.Should().Contain("Response summary: Order rejected: unknown item code ACM-99.");
    }

    [Fact]
    public async Task Dispatcher_SaysNothingAboutAnEmptyBody()
    {
        var result = await DeliverAgainst(HttpStatusCode.BadRequest, "   ");

        result.ErrorMessage.Should().Be("HTTP 400: supplier endpoint returned an error.");
    }

    /// <summary>
    /// The rule itself, stated once over the shapes a remote endpoint really answers with.
    /// Nothing that is not plain prose may be pasted in front of an operator.
    /// </summary>
    [Theory]
    [InlineData("<!DOCTYPE html><html><body>gone</body></html>")]
    [InlineData("<html lang=\"en\"><head><title>502</title></head></html>")]
    [InlineData("<?xml version=\"1.0\"?><fault><code>7</code></fault>")]
    [InlineData("<!-- nginx -->")]
    [InlineData("""{"traceId":"x"}""")]
    [InlineData("""[{"code":1},{"code":2}]""")]
    public void NoMarkupOrRawStructureEverReachesTheOperator(string body)
    {
        var summary = SupplierResponseClassification.SummarizeResponseBody(body);

        summary.Quotable.Should().BeFalse();
        summary.Text.Should().NotBeNull();
        summary.Text.Should().NotContain("<");
        summary.Text.Should().NotContain("{");
        summary.Text.Should().NotContain("[");
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────
// `file`-scoped copies of the two doubles HttpDeliveryDispatcherTests declares. Theirs are
// `file sealed` too, so they are not reachable from here; duplicating the seam is cheaper and
// safer than widening the visibility of another test file's internals.

file sealed class FakeHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}

/// <summary>
/// Test seam: overrides the SSRF-guarded send client (which would open a real socket) with a
/// client backed by an in-memory fake transport.
/// </summary>
file sealed class TestableHttpDeliveryDispatcher : HttpDeliveryDispatcher
{
    private readonly HttpClient _sendClient;

    public TestableHttpDeliveryDispatcher(
        IHttpClientFactory factory, OutboundRequestGuard guard, HttpClient sendClient)
        : base(factory, guard, NullLogger<HttpDeliveryDispatcher>.Instance)
    {
        _sendClient = sendClient;
    }

    internal override HttpClient CreateSendClient() => _sendClient;
}
