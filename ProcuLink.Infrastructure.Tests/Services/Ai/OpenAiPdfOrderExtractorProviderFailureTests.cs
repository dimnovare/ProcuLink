using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// An AI provider outage must not be silent.
///
/// <para>Every failure inside this extractor degrades the caller to the deterministic regex
/// parser, and the caller cannot tell why: a document the model could not read and a revoked API
/// key both arrive as <c>Success=false</c>. Before this, both were logged at Warning, so an
/// OpenAI outage — or a rotated key nobody re-deployed — dropped extraction quality for every
/// upload in every organisation with nothing raised anywhere. The Worker's Sentry integration
/// captures at Error and above, so logging at Error IS the alert.</para>
///
/// <para>The other half matters just as much: an Error that fires on ordinary per-document misses
/// trains people to ignore Sentry. So these tests pin BOTH directions — a 503 is an Error, and a
/// 400 is not.</para>
/// </summary>
public class OpenAiPdfOrderExtractorProviderFailureTests
{
    // ── The classifier, both directions ──────────────────────────────────────

    [Theory]
    [InlineData(0)]     // the exception carried no response at all
    [InlineData(401)]   // key missing, revoked, or rotated out from under us
    [InlineData(403)]   // key not entitled to this model
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public void A_provider_level_status_is_classified_as_a_provider_failure(int status)
    {
        OpenAiPdfOrderExtractor.ClassifyProviderFailureStatus(status)
            .Should().NotBeNull($"HTTP {status} will repeat for the next document too");
    }

    [Theory]
    [InlineData(400)]   // this request's schema/prompt
    [InlineData(404)]   // unknown model — a config error, not an outage
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(429)]   // deliberately excluded: a transient burst limit reads identically to an
                        // exhausted quota without sniffing the body for insufficient_quota
    public void A_request_level_status_is_not_a_provider_failure(int status)
    {
        OpenAiPdfOrderExtractor.ClassifyProviderFailureStatus(status)
            .Should().BeNull($"HTTP {status} describes this call, and an Error on it would be noise");
    }

    [Fact]
    public void An_auth_status_and_an_outage_status_are_told_apart()
    {
        // Not a constant compared to itself: the point is that the two causes carry DIFFERENT
        // names, so an alert can group by cause rather than seeing one undifferentiated blob.
        var auth   = OpenAiPdfOrderExtractor.ClassifyProviderFailureStatus(401);
        var outage = OpenAiPdfOrderExtractor.ClassifyProviderFailureStatus(503);

        auth.Should().NotBe(outage);
        auth.Should().Be(OpenAiPdfOrderExtractor.AuthFailure);
        outage.Should().Be(OpenAiPdfOrderExtractor.ProviderUnavailable);
    }

    [Fact]
    public void A_transport_exception_is_a_provider_failure_however_deeply_it_is_wrapped()
    {
        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new HttpRequestException("connection refused"))
            .Should().Be(OpenAiPdfOrderExtractor.TransportFailure);

        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new SocketException(10061))
            .Should().Be(OpenAiPdfOrderExtractor.TransportFailure);

        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new AuthenticationException("TLS handshake failed"))
            .Should().Be(OpenAiPdfOrderExtractor.TransportFailure);

        // The SDK wraps; the classifier unwraps.
        OpenAiPdfOrderExtractor.ClassifyProviderFailure(
            new InvalidOperationException("outer", new HttpRequestException("DNS failure")))
            .Should().Be(OpenAiPdfOrderExtractor.TransportFailure);
    }

    [Fact]
    public void An_ordinary_extraction_miss_is_not_a_provider_failure()
    {
        OpenAiPdfOrderExtractor.ClassifyProviderFailure(null).Should().BeNull();

        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new JsonException("unexpected token"))
            .Should().BeNull("a malformed model response is this document, not the provider");

        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new InvalidOperationException("no content"))
            .Should().BeNull();

        OpenAiPdfOrderExtractor.ClassifyProviderFailure(new NotSupportedException())
            .Should().BeNull();
    }

    // ── The wiring: the classifier is actually consulted, and Error is raised ─

    [Fact]
    public async Task A_provider_outage_is_logged_at_Error_so_Sentry_sees_it()
    {
        var logger = new CapturingLogger();
        var orgId  = Guid.NewGuid();

        var extractor = ExtractorAnsweredWith(StatusHandler(HttpStatusCode.ServiceUnavailable), logger);

        var result = await extractor.ExtractFromTextAsync("PO Number: PO-1\n1 ABC Widget 4 PCS 12.50", orgId, CancellationToken.None);

        result.Success.Should().BeFalse("the caller still falls back to the deterministic parser");

        var error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain(OpenAiPdfOrderExtractor.ProviderUnavailable);
        error.Message.Should().Contain(orgId.ToString());
    }

    [Fact]
    public async Task A_revoked_or_rotated_key_is_logged_at_Error()
    {
        var logger = new CapturingLogger();

        var extractor = ExtractorAnsweredWith(StatusHandler(HttpStatusCode.Unauthorized), logger);

        await extractor.ExtractFromTextAsync("PO Number: PO-1", Guid.NewGuid(), CancellationToken.None);

        var error = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain(OpenAiPdfOrderExtractor.AuthFailure);
    }

    [Fact]
    public async Task An_unreachable_provider_is_logged_at_Error()
    {
        var logger = new CapturingLogger();

        var extractor = ExtractorAnsweredWith(new ThrowingHandler(), logger);

        await extractor.ExtractFromTextAsync("PO Number: PO-1", Guid.NewGuid(), CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
              .Which.Message.Should().Contain(OpenAiPdfOrderExtractor.TransportFailure);
    }

    /// <summary>
    /// <b>The half that keeps Sentry worth reading.</b> A 400 is this request — a prompt the model
    /// rejected, a schema it would not honour. It repeats for this document and no other, so it
    /// stays a Warning. Without this the fix would be "log Error on any failure", which is the same
    /// silence by a different route.
    /// </summary>
    [Fact]
    public async Task A_rejected_request_stays_at_Warning()
    {
        var logger = new CapturingLogger();

        var extractor = ExtractorAnsweredWith(StatusHandler(HttpStatusCode.BadRequest), logger);

        var result = await extractor.ExtractFromTextAsync("PO Number: PO-1", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error,
            "an Error on an ordinary per-document miss trains people to ignore Sentry");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning,
            "the failure is still recorded, just not as an alert");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A real <see cref="OpenAiPdfOrderExtractor"/> over a real <see cref="ChatClient"/> whose
    /// transport is a fake handler — so the SDK builds and throws its genuine exception types
    /// without a single byte leaving the machine. Retries are off: the handler's answer is final,
    /// and three exponential backoffs would only make the test slow.
    /// </summary>
    private static OpenAiPdfOrderExtractor ExtractorAnsweredWith(HttpMessageHandler handler, ILogger<OpenAiPdfOrderExtractor> logger)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint     = new Uri("https://api.openai.test/v1"),
            Transport    = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy  = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = TimeSpan.FromSeconds(10),
        };

        var client = new ChatClient("gpt-4o-mini", new ApiKeyCredential("sk-test-key"), options);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            })
            .Build();

        return new OpenAiPdfOrderExtractor(cfg, logger, tracker: null, overrideClient: client);
    }

    private static HttpMessageHandler StatusHandler(HttpStatusCode status) => new FixedStatusHandler(status);

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":{"message":"simulated"}}"""),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused (simulated)");
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<OpenAiPdfOrderExtractor>
    {
        public List<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(level, formatter(state, ex)));
    }
}
