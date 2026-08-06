using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Security;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// Shared outbound HTTP authentication applier — the SINGLE implementation of the supplier
/// auth model used by BOTH the delivery dispatcher (push a generated artifact to a supplier
/// endpoint) and the catalog pull service (fetch a supplier's catalog over HTTP). Extracting
/// it (plan 2026-06-12 v2, task B1) guarantees the two channels can never drift on auth
/// behaviour or on a security control.
///
/// Supported methods (the <c>type</c> field of the decrypted credentials JSON):
/// <list type="bullet">
///   <item><c>none</c> (or absent) — no auth applied.</item>
///   <item><c>apikey</c> — adds a custom header (<c>header</c> + <c>value</c>).</item>
///   <item><c>bearer</c> — <c>Authorization: Bearer {token}</c>.</item>
///   <item><c>basic</c> — <c>Authorization: Basic base64(user:pass)</c>.</item>
///   <item><c>oauth2_client_credentials</c> — fetches a fresh token at call time from
///     <c>tokenUrl</c> (form/json request style, body/basic client-auth placement, configurable
///     token JSON path) and applies it as a bearer. The token endpoint is SSRF-guarded with the
///     SAME <see cref="OutboundRequestGuard"/> as the primary request.</item>
/// </list>
///
/// Security: the OAuth token request is validated through <see cref="OutboundRequestGuard.ValidateAsync"/>
/// before any connect, and is sent through the SAME (connect-time-revalidating) <see cref="HttpClient"/>
/// the caller passes in, so a private/metadata token URL is blocked exactly like the primary URL.
/// All failures return SAFE, enumerated messages — never the host, the token endpoint body, the
/// client secret, or any inner exception text.
/// </summary>
public sealed class HttpAuthApplier
{
    private readonly OutboundRequestGuard _guard;
    private readonly ILogger _logger;

    public HttpAuthApplier(OutboundRequestGuard guard, ILogger logger)
    {
        _guard = guard;
        _logger = logger;
    }

    /// <summary>
    /// Applies the configured authentication to <paramref name="request"/>. For the OAuth2
    /// client-credentials flow a fresh token is fetched through <paramref name="client"/> first.
    /// Returns <c>null</c> on success, or a SAFE error message string when auth could not be
    /// applied (e.g. OAuth token fetch failed). The error never leaks credentials or hosts.
    /// </summary>
    public async Task<string?> ApplyAsync(
        HttpRequestMessage request, JsonElement creds, HttpClient client, CancellationToken ct)
    {
        if (creds.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        var type = creds.TryGetProperty("type", out var t) ? t.GetString() : "none";
        switch (type)
        {
            case "apikey":
                if (creds.TryGetProperty("header", out var h) &&
                    creds.TryGetProperty("value", out var v) &&
                    !string.IsNullOrWhiteSpace(h.GetString()))
                {
                    var headerName = h.GetString()!;
                    // The header name and value come from tenant-stored credentials; validate
                    // them for CR/LF/NUL/control-char injection before adding. A header that
                    // fails is dropped (and logged) rather than smuggled into the request.
                    if (!HttpHeaderGuard.TryAdd(request.Headers, headerName, v.GetString()))
                        _logger.LogWarning(
                            "Skipping invalid apikey auth header name '{HeaderName}' (failed CR/LF/token validation).",
                            headerName);
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
                    var encoded = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{username.GetString()}:{password.GetString()}"));
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
    /// OAuth2 client-credentials token fetch. Called at request time so the bearer token is
    /// always fresh (never stored at rest). The token endpoint is SSRF-guarded exactly like the
    /// primary URL. Defaults to the standard flow (form-encoded, client_id+secret in the body,
    /// token read from <c>access_token</c>); request style, client-auth placement, and the
    /// response token path are all overridable for non-standard supplier endpoints.
    /// </summary>
    private async Task<(string? token, string? error)> FetchOAuthTokenAsync(
        JsonElement creds, HttpClient client, CancellationToken ct)
    {
        var tokenUrl = creds.TryGetProperty("tokenUrl", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(tokenUrl))
            return (null, "OAuth token URL is missing from the stored credentials.");

        // SSRF guard the token URL — same protection as the primary URL.
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

        if (OutboundRedirectPolicy.IsRedirect((int)resp.StatusCode))
            return (null, OutboundRedirectPolicy.DescribeTokenEndpointRefusal((int)resp.StatusCode));

        string bodyStr;
        try
        {
            bodyStr = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A token endpoint that streams more than the transport's response cap allows (or that
            // dies mid-body) must become the same SAFE, enumerated failure as any other token
            // problem — never an exception escaping from inside auth application.
            _logger.LogWarning(ex, "OAuth token response could not be read.");
            return (null, "OAuth token response could not be read.");
        }

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
}
