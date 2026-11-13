using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Services.Google;

namespace ProcuLink.Infrastructure.Services.Google;

/// <summary>
/// Google OAuth against fixed public Google hosts (R4), so the <c>"google"</c> client carries no
/// OutboundRequestGuard. Tokens are never logged; nothing here logs at all.
/// </summary>
public sealed class GoogleOAuthClient(IHttpClientFactory http, IOptions<GoogleOptions> options) : IGoogleOAuthClient
{
    public const string HttpClientName = "google";
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        AuthorizeEndpoint + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = options.Value.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = $"openid email {DriveFileScope}",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    public async Task<GoogleGrant> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var body = await RequireAsync(TokenRequest(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }), ct);
        return new GoogleGrant(
            Str(body, "access_token") ?? throw new GoogleAuthException(GoogleAuthException.Unavailable),
            Str(body, "refresh_token"),
            Str(body, "scope") ?? "");
    }

    public async Task<GoogleUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var body = await RequireAsync(request, ct);
        return new GoogleUser(
            Str(body, "sub") ?? throw new GoogleAuthException(GoogleAuthException.Unavailable),
            Str(body, "email") ?? "");
    }

    public async Task<GoogleRefresh> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var (status, body) = await SendAsync(TokenRequest(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }), ct);
        if (status == HttpStatusCode.BadRequest && Str(body, "error") == "invalid_grant") return new GoogleRefresh(null, Revoked: true);
        var token = Str(body, "access_token");
        // Any other refusal is not proof the grant is gone, so it stays retryable rather than forcing a reconnect.
        if ((int)status is < 200 or > 299 || token is null)
            throw new HttpRequestException($"Google token refresh answered HTTP {(int)status}.", null, status);
        return new GoogleRefresh(token, Revoked: false);
    }

    private HttpRequestMessage TokenRequest(Dictionary<string, string> form)
    {
        form["client_id"] = options.Value.ClientId;
        form["client_secret"] = options.Value.ClientSecret;
        return new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
    }

    /// <summary>Connect-time calls: every failure, the network included, is a <see cref="GoogleAuthException"/>.</summary>
    private async Task<JsonElement> RequireAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpStatusCode status;
        JsonElement body;
        try { (status, body) = await SendAsync(request, ct); }
        catch (HttpRequestException) { throw new GoogleAuthException(GoogleAuthException.Unavailable); }
        if ((int)status is < 200 or > 299) throw new GoogleAuthException(Str(body, "error") ?? GoogleAuthException.Unavailable);
        return body;
    }

    /// <summary>A timeout surfaces as <see cref="HttpRequestException"/>, like any other network failure.</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await http.CreateClient(HttpClientName).SendAsync(request, ct))
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonElement body = default;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException) { }
                return (response.StatusCode, body);
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Google did not answer in time.", ex);
        }
    }

    private static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
