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

    // Sends Idempotency-Key + X-Message-Id. Whether the supplier honours them is their choice,
    // so this is best-effort, not a guarantee — re-drive, and document the residual.
    public ResendSafety ResendSafety => ResendSafety.BestEffort;

    // Reads the response body on every non-2xx and passes it through verbatim, so a blank
    // ResponseBody here is real evidence that the endpoint returned nothing — which is what makes
    // the bare-400 rule meaningful on this channel. This was the ONLY dispatcher for which that was
    // true before WP-19's follow-up.
    public bool CapturesSupplierResponseBody => true;

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
        CancellationToken ct,
        string? idempotencyKey = null)
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

            // ── A3 idempotency ────────────────────────────────────────────────
            // Carry the deterministic per-artifact idempotency key so a supplier that honours it
            // treats a crash-recovery re-send (or a duplicated activation) as a no-op. The key is a
            // ProcuLink-generated token (no CR/LF/control chars), added before the tenant-supplied
            // extra headers so it cannot be silently overwritten by supplier config. Sent both as the
            // conventional `Idempotency-Key` and a stable `X-Message-Id` for suppliers that key on the
            // latter.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                request.Headers.TryAddWithoutValidation("X-Message-Id", idempotencyKey);
            }

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
                // while ErrorMessage stays a short human-readable summary. Retry-After is the
                // supplier's own back-pressure and is honoured as a FLOOR on the next backoff step.
                : new DeliveryResult(
                    false, BuildFailureMessage(code, body), code,
                    ResponseBody: NullIfBlank(body),
                    SupplierReasonObservable: CapturesSupplierResponseBody,
                    RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
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

    /// <summary>
    /// The sentence an operator reads when the response code is one
    /// <see cref="SupplierResponseClassification"/> has no named hint for — DescribeFailure
    /// returns this message unchanged in that case, so it is a real operator-facing surface and
    /// not merely a log line.
    ///
    /// <para>It used to slice its own 120 characters off the raw body, which is how a web
    /// server's HTML error page reached a person cut off mid-tag (WP-39 §4.4). The decision about
    /// what a remote body may say now lives in exactly one place, shared with the other
    /// passthrough. The full body still travels verbatim on <c>DeliveryResult.ResponseBody</c>.</para>
    /// </summary>
    private static string BuildFailureMessage(int code, string body)
    {
        var opening = $"HTTP {code}: supplier endpoint returned an error.";
        var summary = SupplierResponseClassification.SummarizeResponseBody(body);

        if (summary.Text is null) return opening;

        return summary.Quotable
            ? $"{opening} Response summary: {summary.Text}"
            : $"{opening} {summary.Text}";
    }

    // ── Private config POCO ───────────────────────────────────────────────────

    private record HttpConfig(
        string Url,
        string? Method,
        Dictionary<string, string>? Headers,
        int? TimeoutSeconds);
}
