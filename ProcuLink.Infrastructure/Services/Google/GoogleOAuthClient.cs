using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Google;
using static ProcuLink.Infrastructure.Services.OAuthHttp;

namespace ProcuLink.Infrastructure.Services.Google;

/// <summary>
/// Google OAuth against fixed public Google hosts (R4), so the <c>"google"</c> client carries no
/// OutboundRequestGuard. Tokens are never logged; nothing here logs at all.
/// </summary>
public sealed class GoogleOAuthClient(IHttpClientFactory http, IOptions<GoogleOptions> options) : ILinkedAccountOAuth
{
    public const string HttpClientName = "google";
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    public string Provider => LinkedAccountProviders.Google;

    public bool IsConfigured => options.Value.IsConfigured;

    // Granular consent lets the user untick Drive; without it no destination could work.
    public bool HasRequiredScope(string grantedScope) =>
        grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DriveFileScope);

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

    public async Task<OAuthGrant> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var body = await RequireAsync(Client, TokenRequest(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }), "Google", ct);
        return new OAuthGrant(
            Str(body, "access_token") ?? throw new OAuthException(OAuthException.Unavailable),
            Str(body, "refresh_token"),
            Str(body, "scope") ?? "");
    }

    public async Task<OAuthUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var body = await RequireAsync(Client, request, "Google", ct);
        return new OAuthUser(
            Str(body, "sub") ?? throw new OAuthException(OAuthException.Unavailable),
            Str(body, "email") ?? "");
    }

    public async Task<OAuthRefresh> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var (status, body) = await SendAsync(Client, TokenRequest(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }), "Google", ct);
        if (status == HttpStatusCode.BadRequest && Str(body, "error") == "invalid_grant") return new OAuthRefresh(null, Revoked: true);
        var token = Str(body, "access_token");
        // Any other refusal is not proof the grant is gone, so it stays retryable rather than forcing a reconnect.
        if ((int)status is < 200 or > 299 || token is null)
            throw new HttpRequestException($"Google token refresh answered HTTP {(int)status}.", null, status);
        return new OAuthRefresh(token, Revoked: false);
    }

    private HttpClient Client => http.CreateClient(HttpClientName);

    private HttpRequestMessage TokenRequest(Dictionary<string, string> form)
    {
        form["client_id"] = options.Value.ClientId;
        form["client_secret"] = options.Value.ClientSecret;
        return new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
    }
}
