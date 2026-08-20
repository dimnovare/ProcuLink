using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// Transport policy at the SEND-time end of the OAuth token exchange.
///
/// <para>The save path now refuses a cleartext <c>tokenUrl</c>, but refusing at save protects
/// nothing already stored. A credential encrypted before enforcement existed keeps working —
/// silently failing those deliveries would trade a security weakness for an outage — so
/// <see cref="HttpAuthApplier"/> follows the dispatch-time convention
/// (<c>HttpDeliveryDispatcher.WarnIfInsecureTransport</c>) EXACTLY: log a warning once per
/// attempt and continue. The warning never carries the full URL — the refusal that most needs
/// surfacing is a userinfo URL, and logging it whole would put the password into the log line —
/// and never the client secret.</para>
/// </summary>
public class HttpAuthApplierTokenUrlTransportTests
{
    private static OutboundRequestGuard PermissiveGuard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static JsonElement Creds(object o) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(o));

    private static RoutingHandler TokenIssuingHandler() => new(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"access_token\":\"tok-legacy\",\"expires_in\":3600}") });

    /// <summary>
    /// The pinned legacy behaviour: a stored cleartext token URL still authenticates — the token
    /// is fetched and applied — and the operator learns about it from a warning, not an outage.
    /// </summary>
    [Fact]
    public async Task OAuth2_LegacyHttpTokenUrl_WarnsAndContinues()
    {
        var logger = new CapturingLogger();
        var applier = new HttpAuthApplier(PermissiveGuard(), logger);
        var req = new HttpRequestMessage(HttpMethod.Post, "https://supplier.example/orders");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "http://auth.supplier.example/oauth/token",
            clientId = "cid", clientSecret = "s3cr3t-value",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(TokenIssuingHandler()), default);

        error.Should().BeNull("a config that predates enforcement must keep delivering");
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization!.Parameter.Should().Be("tok-legacy");

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().NotBeEmpty("the operator must be told the token endpoint no longer passes policy");

        var warning = string.Join(" ", warnings.Select(w => w.Message));
        warning.Should().NotContain("/oauth/token", "the warning must not log the full token URL");
        warning.Should().NotContain("s3cr3t-value", "the warning must never carry the client secret");
    }

    /// <summary>The negative control: a compliant https token URL produces no warning at all.</summary>
    [Fact]
    public async Task OAuth2_HttpsTokenUrl_ProducesNoTransportWarning()
    {
        var logger = new CapturingLogger();
        var applier = new HttpAuthApplier(PermissiveGuard(), logger);
        var req = new HttpRequestMessage(HttpMethod.Post, "https://supplier.example/orders");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "https://auth.supplier.example/oauth/token",
            clientId = "cid", clientSecret = "s3cr3t-value",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(TokenIssuingHandler()), default);

        error.Should().BeNull();
        req.Headers.Authorization!.Parameter.Should().Be("tok-legacy");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().BeEmpty();
    }

    /// <summary>
    /// Loopback http is allowed by the shared policy on purpose (local dev, e2e listeners) —
    /// warning on it would train operators to ignore the warning that matters.
    /// </summary>
    [Fact]
    public async Task OAuth2_LoopbackHttpTokenUrl_ProducesNoTransportWarning()
    {
        var logger = new CapturingLogger();
        var applier = new HttpAuthApplier(PermissiveGuard(), logger);
        var req = new HttpRequestMessage(HttpMethod.Post, "https://supplier.example/orders");
        var creds = Creds(new
        {
            type = "oauth2_client_credentials",
            tokenUrl = "http://127.0.0.1:5223/oauth/token",
            clientId = "cid", clientSecret = "s3cr3t-value",
        });

        var error = await applier.ApplyAsync(req, creds, new HttpClient(TokenIssuingHandler()), default);

        error.Should().BeNull();
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().BeEmpty();
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private sealed class CapturingLogger : ILogger
    {
        public sealed record Entry(LogLevel Level, string Message);

        public List<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
