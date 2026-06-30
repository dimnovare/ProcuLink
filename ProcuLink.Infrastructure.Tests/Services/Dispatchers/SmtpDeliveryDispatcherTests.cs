using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class SmtpDeliveryDispatcherTests
{
    // Build a guard with AllowPrivateNetworkTargets=true so existing unit tests
    // that use fictional hostnames (smtp.vendor.test, etc.) are not blocked by
    // DNS resolution — the guard SSRF logic is tested separately in
    // OutboundRequestGuardHostTests and OutboundRequestGuardTests.
    private static OutboundRequestGuard AllowAllGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static SmtpDeliveryDispatcher Dispatcher() =>
        new(NullLogger<SmtpDeliveryDispatcher>.Instance, AllowAllGuard());

    private static SupplierDeliveryConfig MakeConfig(object config) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            Protocol = DeliveryProtocolConstants.Smtp,
            AutoDeliver = false,
            ConfigJson = JsonSerializer.Serialize(config),
            EncryptedCredentials = string.Empty,
        };

    private static string Creds(string username = "smtp-user", string password = "secret") =>
        JsonSerializer.Serialize(new { username, password });

    [Fact]
    public void Protocol_IsSmtp()
    {
        Dispatcher().Protocol.Should().Be("smtp");
        Dispatcher().Protocol.Should().Be(DeliveryProtocolConstants.Smtp);
    }

    [Fact]
    public async Task Dispatch_BlankHost_ReturnsFailure_DoesNotThrow()
    {
        var config = MakeConfig(new { host = "", fromAddress = "po@buyer.test", toAddresses = "supplier@vendor.test" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery configuration is invalid — host is required.");
    }

    [Fact]
    public async Task Dispatch_MalformedConfigJson_ReturnsParseFailure()
    {
        var config = MakeConfig(new { });
        config.ConfigJson = "{not-json";

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery configuration could not be parsed.");
    }

    [Fact]
    public async Task Dispatch_NoRecipients_ReturnsFailure()
    {
        var config = MakeConfig(new { host = "smtp.vendor.test", fromAddress = "po@buyer.test", toAddresses = "" });

        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, Creds(), default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery has no valid recipient addresses.");
    }

    [Fact]
    public async Task Dispatch_MissingCredentials_ReturnsFailure()
    {
        var config = MakeConfig(new
        {
            host = "smtp.vendor.test",
            fromAddress = "po@buyer.test",
            toAddresses = "supplier@vendor.test",
        });

        // Empty decryptedCredentials → credentials missing.
        var result = await Dispatcher().DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, string.Empty, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SMTP delivery credentials are missing — username is required.");
    }

    [Fact]
    public void TokenSubstitution_ReplacesPoNumberAndFileName()
    {
        var subject = SmtpDeliveryDispatcher.BuildFromTemplate(
            "PO {poNumber} attached as {fileName}", "DEMO-2026-001", "DEMO-2026-001.xml");

        subject.Should().Be("PO DEMO-2026-001 attached as DEMO-2026-001.xml");
    }

    [Fact]
    public void TokenSubstitution_NoTokens_ReturnsTemplateUnchanged()
    {
        var subject = SmtpDeliveryDispatcher.BuildFromTemplate(
            "Static subject line", "DEMO-2026-001", "order.csv");

        subject.Should().Be("Static subject line");
    }

    // ── Postmark HTTPS transport ────────────────────────────────────────────

    private static IConfiguration ConfigWithPostmarkToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:Smtp:PostmarkServerToken"] = token,
            })
            .Build();

    private static SupplierDeliveryConfig PostmarkConfig() =>
        MakeConfig(new
        {
            host = "smtp.vendor.test",
            fromAddress = "po@buyer.test",
            toAddresses = "supplier@vendor.test, ops@vendor.test",
        });

    [Fact]
    public async Task Postmark_200_ErrorCode0_ReturnsSuccess()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK, "{\"ErrorCode\":0,\"Message\":\"OK\"}");

        var dispatcher = new TestablePostmarkSmtpDispatcher(
            AllowAllGuard(), ConfigWithPostmarkToken("pm-token-123"), handler);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("PO,DATE\r\n001,2026-01-01"),
            "order.csv", "text/csv", PostmarkConfig(), Creds(), default);

        result.Success.Should().BeTrue();
        result.ResponseCode.Should().Be(200);
        result.ErrorMessage.Should().BeNull();

        // Sent to the Postmark HTTPS endpoint (not raw SMTP).
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://api.postmarkapp.com/email");
        handler.LastRequest.Headers.GetValues("X-Postmark-Server-Token").Should().ContainSingle()
            .Which.Should().Be("pm-token-123");

        // Body reuses From/recipients/attachment from the SMTP config.
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        root.GetProperty("From").GetString().Should().Be("po@buyer.test");
        root.GetProperty("To").GetString().Should().Be("supplier@vendor.test,ops@vendor.test");
        root.GetProperty("MessageStream").GetString().Should().Be("outbound");
        var attachment = root.GetProperty("Attachments")[0];
        attachment.GetProperty("Name").GetString().Should().Be("order.csv");
        attachment.GetProperty("ContentType").GetString().Should().Be("text/csv");
        // Content is base64 of the artifact bytes.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(attachment.GetProperty("Content").GetString()!));
        decoded.Should().Be("PO,DATE\r\n001,2026-01-01");
    }

    [Fact]
    public async Task Postmark_422_NonZeroErrorCode_ReturnsFailureWithMessage()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.UnprocessableEntity,
            "{\"ErrorCode\":300,\"Message\":\"Invalid 'From' address.\"}");

        var dispatcher = new TestablePostmarkSmtpDispatcher(
            AllowAllGuard(), ConfigWithPostmarkToken("pm-token-123"), handler);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", PostmarkConfig(), Creds(), default);

        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(422);
        result.ErrorMessage.Should().Contain("Invalid 'From' address.");
    }

    [Fact]
    public async Task NoPostmarkToken_UsesMailKitPath_NotPostmark()
    {
        // No token configured → the Postmark HTTPS path must NOT be taken. The HttpClient
        // seam would throw if used; instead the dispatcher falls through to the MailKit
        // raw-SMTP path, which (with AllowAllGuard + a fictional host) fails at connect/send
        // with an SMTP-flavoured error — proving Postmark was never invoked.
        var handler = new ThrowingHttpMessageHandler();
        var dispatcher = new TestablePostmarkSmtpDispatcher(
            AllowAllGuard(), ConfigWithPostmarkToken(""), handler);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", PostmarkConfig(), Creds(), default);

        // MailKit path was taken: failure is NOT a Postmark error.
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotContain("Postmark");
        handler.WasCalled.Should().BeFalse();
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

/// <summary>
/// Captures the outbound Postmark request (URI, headers, body) and returns a canned
/// status + body. Mirrors the fake-handler doubles in <c>HttpDeliveryDispatcherTests</c>.
/// </summary>
file sealed class CapturingHttpMessageHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}

/// <summary>Throws if invoked — proves the HTTPS (Postmark) path was NOT taken.</summary>
file sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    public bool WasCalled { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasCalled = true;
        throw new InvalidOperationException("HTTPS transport must not be used when no Postmark token is configured.");
    }
}

/// <summary>
/// Test seam: overrides the SSRF-guarded send client (which would open a real socket) with a
/// client backed by an in-memory fake transport, mirroring <c>TestableHttpDeliveryDispatcher</c>.
/// An <see cref="IHttpClientFactory"/> is supplied so the Postmark branch's guard
/// (<c>_httpClientFactory is not null</c>) is satisfied; the overridden <c>CreateSendClient</c>
/// means the factory's client is never actually used for the send.
/// </summary>
file sealed class TestablePostmarkSmtpDispatcher : SmtpDeliveryDispatcher
{
    private readonly HttpClient _sendClient;

    public TestablePostmarkSmtpDispatcher(
        OutboundRequestGuard guard, IConfiguration configuration, HttpMessageHandler handler)
        : base(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpDeliveryDispatcher>.Instance,
            guard,
            new StubHttpClientFactory(),
            configuration)
    {
        _sendClient = new HttpClient(handler);
    }

    internal override HttpClient CreateSendClient() => _sendClient;

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        // Returns a throwaway client; the timeout it carries is copied by the real
        // CreateSendClient, which the test overrides anyway, so this is never used to send.
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(30) };
    }
}
