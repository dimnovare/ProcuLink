using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

public class HttpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OutboundRequestGuard _guard;
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

            // Apply auth (oauth2 mode fetches a fresh token first)
            var authError = await ApplyAuthAsync(request, creds, client, requestCt);
            if (authError is not null)
                return new DeliveryResult(false, authError);

            // Apply extra headers
            if (httpCfg.Headers is not null)
                foreach (var (k, v) in httpCfg.Headers)
                    request.Headers.TryAddWithoutValidation(k, v);

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

    private async Task<string?> ApplyAuthAsync(HttpRequestMessage request, JsonElement creds, HttpClient client, CancellationToken ct)
    {
        if (creds.ValueKind == JsonValueKind.Undefined) return null;

        var type = creds.TryGetProperty("type", out var t) ? t.GetString() : "none";
        switch (type)
        {
            case "apikey":
                if (creds.TryGetProperty("header", out var h) &&
                    creds.TryGetProperty("value", out var v) &&
                    !string.IsNullOrWhiteSpace(h.GetString()))
                {
                    var headerName = h.GetString()!;
                    request.Headers.TryAddWithoutValidation(headerName, v.GetString());
                }
                break;

            case "bearer":
                if (creds.TryGetProperty("token", out var token) &&
                    !string.IsNullOrWhiteSpace(token.GetString()))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", token.GetString());
                }
                break;

            case "basic":
                if (creds.TryGetProperty("username", out var username) &&
                    creds.TryGetProperty("password", out var password))
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username.GetString()}:{password.GetString()}"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Basic", encoded);
                }
                break;

            case "oauth2_client_credentials":
                var (oauthToken, oauthError) = await FetchOAuthTokenAsync(creds, client, ct);
                if (oauthError is not null) return oauthError;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
                break;
        }

        return null;
    }

    /// <summary>
    /// OAuth2 client-credentials token fetch. Called at delivery time so the bearer token is
    /// always fresh (never stored at rest). The token endpoint is SSRF-guarded exactly like the
    /// delivery URL. Defaults to the standard flow (form-encoded, client_id+secret in the body,
    /// token read from <c>access_token</c>); request style, client-auth placement, and the
    /// response token path are all overridable for non-standard supplier endpoints.
    /// </summary>
    private async Task<(string? token, string? error)> FetchOAuthTokenAsync(JsonElement creds, HttpClient client, CancellationToken ct)
    {
        var tokenUrl = creds.TryGetProperty("tokenUrl", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(tokenUrl))
            return (null, "OAuth token URL is missing from delivery credentials.");

        // SSRF guard the token URL — same protection as the delivery URL.
        var guard = await _guard.ValidateAsync(tokenUrl, ct);
        if (!guard.Allowed)
            return (null, $"OAuth token request blocked: {guard.Reason}");

        string Get(string name) => creds.TryGetProperty(name, out var e) ? e.GetString() ?? "" : "";
        var clientId     = Get("clientId");
        var clientSecret = Get("clientSecret");
        var scope        = Get("scope");
        var grantType    = string.IsNullOrWhiteSpace(Get("grantType")) ? "client_credentials" : Get("grantType");
        var authStyle    = string.IsNullOrWhiteSpace(Get("authStyle")) ? "body" : Get("authStyle");
        var requestStyle = string.IsNullOrWhiteSpace(Get("requestStyle")) ? "form" : Get("requestStyle");
        var tokenPath    = string.IsNullOrWhiteSpace(Get("tokenResponsePath")) ? "access_token" : Get("tokenResponsePath");
        var useBasic     = string.Equals(authStyle, "basic", StringComparison.OrdinalIgnoreCase);

        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        if (useBasic)
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        if (string.Equals(requestStyle, "json", StringComparison.OrdinalIgnoreCase))
        {
            var payload = new Dictionary<string, string> { ["grant_type"] = grantType };
            if (!string.IsNullOrWhiteSpace(scope)) payload["scope"] = scope;
            if (!useBasic) { payload["client_id"] = clientId; payload["client_secret"] = clientSecret; }
            tokenRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        else
        {
            var form = new List<KeyValuePair<string, string>> { new("grant_type", grantType) };
            if (!string.IsNullOrWhiteSpace(scope)) form.Add(new("scope", scope));
            if (!useBasic) { form.Add(new("client_id", clientId)); form.Add(new("client_secret", clientSecret)); }
            tokenRequest.Content = new FormUrlEncodedContent(form);
        }

        HttpResponseMessage resp;
        try { resp = await client.SendAsync(tokenRequest, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OAuth token request failed before a response.");
            return (null, "OAuth token request failed before a response was received.");
        }

        var bodyStr = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (null, $"OAuth token request failed: HTTP {(int)resp.StatusCode}.");

        try
        {
            using var doc = JsonDocument.Parse(bodyStr);
            var el = doc.RootElement;
            foreach (var seg in tokenPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg, out el))
                    return (null, $"OAuth token response did not contain a token at '{tokenPath}'.");

            var resolved = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            return string.IsNullOrWhiteSpace(resolved)
                ? (null, $"OAuth token response did not contain a token at '{tokenPath}'.")
                : (resolved, null);
        }
        catch (JsonException)
        {
            return (null, "OAuth token response was not valid JSON.");
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
