using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services.Email;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Email;

// ════════════════════════════════════════════════════════════════════════════
//  PostmarkEmailApiClient — the HTTPS email-API client (Postmark) that replaces
//  outbound SMTP on hosts that block port 25/587 (Railway). These tests pin:
//    • the no-op-when-unconfigured contract (safe default deploy);
//    • Postmark's 200-with-non-zero-ErrorCode = failure quirk;
//    • the request shape (token header, comma-joined To, base64 attachment,
//      MessageStream) so a wire-format regression is caught without a live send;
//    • DefaultFrom config fallback chain.
//  All HTTP is faked via a file-scoped HttpMessageHandler; CreateClient("email")
//  is stubbed on a Mock<IHttpClientFactory>.
// ════════════════════════════════════════════════════════════════════════════
public class PostmarkEmailApiClientTests
{
    private const string Token = "test-server-token-123";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs)
            dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static PostmarkEmailApiClient MakeClient(IConfiguration config, HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(PostmarkEmailApiClient.HttpClientName))
               .Returns(() => new HttpClient(handler));
        return new PostmarkEmailApiClient(config, factory.Object, NullLogger<PostmarkEmailApiClient>.Instance);
    }

    private static EmailApiMessage SimpleMessage(params string[] to) =>
        new(From: "from@proculink.eu", To: to, Subject: "PO 1", TextBody: "body");

    // 1. No token → not configured, no HTTP call attempted.
    [Fact]
    public async Task NoToken_IsNotConfigured_AndReturnsNotConfiguredWithoutHttp()
    {
        var client = MakeClient(Config(), new ThrowingHandler());

        client.IsConfigured.Should().BeFalse();

        var result = await client.SendAsync(SimpleMessage("a@x.example"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not configured");
    }

    // 2. Token + HTTP 200 with ErrorCode 0 → success.
    [Fact]
    public async Task Configured_Http200_ErrorCode0_ReturnsSuccess()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"ErrorCode":0,"Message":"OK","MessageID":"x"}""");
        var client = MakeClient(Config(("Email:Postmark:ServerToken", Token)), handler);

        client.IsConfigured.Should().BeTrue();

        var result = await client.SendAsync(SimpleMessage("a@x.example"));

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Error.Should().BeNull();
    }

    // 3. Token + HTTP 200 but ErrorCode 300 (inactive recipient) → failure, status still 200.
    [Fact]
    public async Task Configured_Http200_NonZeroErrorCode_ReturnsFailureWithProviderMessage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"ErrorCode":300,"Message":"Inactive recipient"}""");
        var client = MakeClient(Config(("Email:Postmark:ServerToken", Token)), handler);

        var result = await client.SendAsync(SimpleMessage("a@x.example"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Inactive recipient");
        result.StatusCode.Should().Be(200);
    }

    // 4. Token + HTTP 422 → failure carrying the 422 status code.
    [Fact]
    public async Task Configured_Http422_ReturnsFailureWith422()
    {
        var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, """{"ErrorCode":300,"Message":"Bad"}""");
        var client = MakeClient(Config(("Email:Postmark:ServerToken", Token)), handler);

        var result = await client.SendAsync(SimpleMessage("a@x.example"));

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(422);
    }

    // 5. Empty recipient list → failure, no HTTP call.
    [Fact]
    public async Task EmptyRecipients_ReturnsFailureWithoutHttp()
    {
        var client = MakeClient(Config(("Email:Postmark:ServerToken", Token)), new ThrowingHandler());

        var result = await client.SendAsync(SimpleMessage()); // empty To

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("no recipient");
    }

    // 6. The wire request: token header + comma-joined To + base64 attachment + outbound MessageStream.
    [Fact]
    public async Task SerializesRequestWithTokenHeaderRecipientsAttachmentAndStream()
    {
        var capture = new CapturingHandler(HttpStatusCode.OK, """{"ErrorCode":0,"Message":"OK"}""");
        var client = MakeClient(Config(("Email:Postmark:ServerToken", Token)), capture);

        var attachmentBytes = Encoding.UTF8.GetBytes("PO,DATE\r\n001,2026-01-01");
        var expectedBase64 = Convert.ToBase64String(attachmentBytes);
        var message = new EmailApiMessage(
            From: "from@proculink.eu",
            To: new[] { "a@x.example", "b@y.example" },
            Subject: "PO 1",
            TextBody: "body",
            Attachments: new[] { new EmailApiAttachment("order.csv", "text/csv", attachmentBytes) });

        var result = await client.SendAsync(message);

        result.Success.Should().BeTrue();

        capture.LastRequest.Should().NotBeNull();
        capture.LastRequest!.Headers.TryGetValues("X-Postmark-Server-Token", out var tokenValues).Should().BeTrue();
        tokenValues!.Should().ContainSingle().Which.Should().Be(Token);

        capture.LastBody.Should().NotBeNull();
        capture.LastBody.Should().Contain("a@x.example,b@y.example");   // comma-joined recipients
        capture.LastBody.Should().Contain(expectedBase64);       // base64 of the attachment
        capture.LastBody.Should().Contain("\"MessageStream\":\"outbound\"");
    }

    // 7. DefaultFrom resolution: Smtp:From fallback, then the hard default.
    [Fact]
    public void DefaultFrom_FallsBackToSmtpFrom_ThenHardDefault()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new ThrowingHandler()));

        var withSmtpFrom = new PostmarkEmailApiClient(
            Config(("Smtp:From", "smtp-from@example.com")), factory.Object,
            NullLogger<PostmarkEmailApiClient>.Instance);
        withSmtpFrom.DefaultFrom.Should().Be("smtp-from@example.com");

        var withNothing = new PostmarkEmailApiClient(
            Config(), factory.Object, NullLogger<PostmarkEmailApiClient>.Instance);
        withNothing.DefaultFrom.Should().Be("orders@proculink.eu");

        var withPostmarkFrom = new PostmarkEmailApiClient(
            Config(("Email:Postmark:From", "postmark-from@example.com"), ("Smtp:From", "smtp-from@example.com")),
            factory.Object, NullLogger<PostmarkEmailApiClient>.Instance);
        withPostmarkFrom.DefaultFrom.Should().Be("postmark-from@example.com");
    }
}

/// <summary>Returns a fixed status + JSON body for any request.</summary>
file sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        });
}

/// <summary>Captures the last request + its serialized body, then returns a fixed response.</summary>
file sealed class CapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    public CapturingHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Fails the test if the HTTP path is ever hit (asserts no-HTTP code paths).</summary>
file sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("HTTP must not be called on this code path.");
}
