using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

public class HttpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OutboundRequestGuard _guard;
    private readonly HttpAuthApplier _auth;
    private readonly ILogger<HttpDeliveryDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => "http";

    public HttpDeliveryDispatcher(
        IHttpClientFactory httpClientFactory,
        OutboundRequestGuard guard,
        ILogger<HttpDeliveryDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _guard = guard;
        // The shared auth applier owns the exact none/apikey/bearer/basic/oauth2 model and the
        // SSRF-guarded OAuth token fetch — one implementation shared with the catalog HTTP pull.
        _auth = new HttpAuthApplier(guard, logger);
        _logger = logger;
    }

    // Built lazily once from the guard's connect-time-revalidating handler and reused
    // for the lifetime of this (scoped) dispatcher. SocketsHttpHandler pools connections.
    private HttpClient? _guardedClient;

    /// <summary>
    /// Resolves the <see cref="HttpClient"/> used for the outbound delivery (and OAuth token)
    /// request. The default wraps the named <c>delivery</c> handler config (timeout) but swaps
    /// in the guard's connect-time-revalidating <see cref="SocketsHttpHandler"/>, so a DNS-rebind
    /// to a private/metadata IP after the up-front <see cref="OutboundRequestGuard.ValidateAsync"/>
    /// is still rejected at TCP connect. Tests override this to inject a fake transport.
    /// </summary>
    internal virtual HttpClient CreateSendClient()
    {
        if (_guardedClient is not null) return _guardedClient;

        // Mirror the "delivery" named-client timeout so behaviour is unchanged for the happy path,
        // but route the socket through the SSRF connect-time re-validation.
        var timeout = _httpClientFactory.CreateClient("delivery").Timeout;
        _guardedClient = new HttpClient(_guard.CreateGuardedHttpHandler(), disposeHandler: true)
        {
            Timeout = timeout,
        };
        return _guardedClient;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct)
    {
        try
        {
            var httpCfg = JsonSerializer.Deserialize<HttpConfig>(config.ConfigJson, JsonOpts);
            if (httpCfg is null || string.IsNullOrWhiteSpace(httpCfg.Url))
                return new DeliveryResult(false, "HTTP delivery configuration is invalid.");

            if (!Uri.TryCreate(httpCfg.Url, UriKind.Absolute, out var endpoint))
                return new DeliveryResult(false, "HTTP delivery endpoint URL is invalid.");

            // ── SSRF guard — must pass before any outbound request ────────────
            var guardResult = await _guard.ValidateAsync(httpCfg.Url, ct);
            if (!guardResult.Allowed)
            {
                _logger.LogWarning(
                    "HTTP delivery blocked by SSRF guard for URL '{Url}': {Reason}",
                    httpCfg.Url, guardResult.Reason);
                return new DeliveryResult(false, $"Delivery blocked: {guardResult.Reason}");
            }

            var creds = string.IsNullOrEmpty(decryptedCredentials)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(decryptedCredentials, JsonOpts);

            var client  = CreateSendClient();
            var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(httpCfg.Method) ? "POST" : httpCfg.Method),
                endpoint);

            using var timeoutCts = httpCfg.TimeoutSeconds is > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(httpCfg.TimeoutSeconds!.Value));
            var requestCt = timeoutCts?.Token ?? ct;

            // Apply auth (oauth2 mode fetches a fresh token first) via the shared applier.
            var authError = await _auth.ApplyAsync(request, creds, client, requestCt);
            if (authError is not null)
                return new DeliveryResult(false, authError);

            // Apply extra headers. Names+values are tenant-supplied, so each is validated for
            // CR/LF/NUL/control-char injection before it can reach the request — a header that
            // fails is skipped (and logged), never smuggled into the outbound request.
            if (httpCfg.Headers is not null)
                foreach (var (k, v) in httpCfg.Headers)
                    if (!HttpHeaderGuard.TryAdd(request.Headers, k, v))
                        _logger.LogWarning(
                            "Skipping invalid delivery header name '{HeaderName}' (failed CR/LF/token validation).", k);

            // Body
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType =
                MediaTypeHeaderValue.TryParse(contentType, out var mt) ? mt : new MediaTypeHeaderValue("application/octet-stream");

            var response = await client.SendAsync(request, requestCt);
            var body     = await response.Content.ReadAsStringAsync(requestCt);
            var code     = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new DeliveryResult(true, null, code)
                // Pass the full (DeliveryService-bounded) body as ResponseBody for rejection capture,
                // while ErrorMessage stays a short human-readable summary.
                : new DeliveryResult(false, BuildFailureMessage(code, body), code, ResponseBody: NullIfBlank(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, "HTTP delivery timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP delivery failed before a response was received.");
            return new DeliveryResult(false, "HTTP delivery failed before receiving a response.");
        }
    }

    private static string? NullIfBlank(string? body) =>
        string.IsNullOrWhiteSpace(body) ? null : body;

    private static string BuildFailureMessage(int code, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"HTTP {code}: supplier endpoint returned an error.";

        var summary = body
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (summary.Length > 120)
            summary = summary[..120];

        return $"HTTP {code}: supplier endpoint returned an error. Response summary: {summary}";
    }

    // ── Private config POCO ───────────────────────────────────────────────────

    private record HttpConfig(
        string Url,
        string? Method,
        Dictionary<string, string>? Headers,
        int? TimeoutSeconds);
}
