using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Google;
using static ProcuLink.Infrastructure.Services.OAuthHttp;

namespace ProcuLink.Infrastructure.Services.Microsoft;

/// <summary>One use of the one Microsoft app: its provider key, the scopes it asks for, and the scope it cannot work without.</summary>
public sealed record MicrosoftProfile(string Provider, string Scopes, string RequiredScope)
{
    /// <summary>Mailbox pickup: read unread mail and mark it read.</summary>
    public static readonly MicrosoftProfile Mailbox = new(LinkedAccountProviders.Outlook,
        "offline_access https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.ReadWrite", "Mail.ReadWrite");

    /// <summary>OneDrive delivery (plan 30 Part B): add files to a folder the user pastes a link to.</summary>
    public static readonly MicrosoftProfile Files = new(LinkedAccountProviders.Microsoft,
        "offline_access https://graph.microsoft.com/User.Read https://graph.microsoft.com/Files.ReadWrite", "Files.ReadWrite");
}

/// <summary>
/// Microsoft identity platform v2 on the <c>/common</c> authority, plus Graph <c>/me</c>, against fixed public hosts, so the
/// <c>"microsoft"</c> client carries no OutboundRequestGuard. Microsoft rotates refresh tokens; <see cref="RefreshAsync"/>
/// returns the new one. Nothing here logs.
/// </summary>
public sealed class MicrosoftOAuthClient(MicrosoftProfile profile, IHttpClientFactory http, IOptions<MicrosoftOptions> options) : ILinkedAccountOAuth
{
    public const string HttpClientName = "microsoft";
    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    private const string MeEndpoint = "https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName";

    /// <summary>Refresh errors that prove only a new sign-in can heal the account.</summary>
    private static readonly string[] Dead = ["invalid_grant", "interaction_required", "consent_required"];

    public string Provider => profile.Provider;

    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>Whole tokens only, so <c>Mail.Read</c> never passes for <c>Mail.ReadWrite</c>; Graph may prefix its resource.</summary>
    public bool HasRequiredScope(string grantedScope)
    {
        string[] accepted = profile == MicrosoftProfile.Files ? [profile.RequiredScope, profile.RequiredScope + ".All"] : [profile.RequiredScope];
        return grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => accepted.Any(a =>
            t.Equals(a, StringComparison.OrdinalIgnoreCase) || t.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)));
    }

    // Never prompt=consent: it forces the consent screen even after an admin approved, which blocks users whose tenant forbids user consent.
    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        Authority + "/authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = options.Value.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = profile.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    public async Task<OAuthGrant> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var body = await RequireAsync(Client, TokenRequest(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }), "Microsoft", ct);
        return new OAuthGrant(
            Str(body, "access_token") ?? throw new OAuthException(OAuthException.Unavailable),
            Str(body, "refresh_token"),
            Str(body, "scope") ?? ""); // no scope in the answer is no permission, never everything asked for
    }

    public async Task<OAuthUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var body = await RequireAsync(Client, request, "Microsoft", ct);
        return new OAuthUser(
            Str(body, "id") ?? throw new OAuthException(OAuthException.Unavailable),
            Str(body, "mail") ?? Str(body, "userPrincipalName") ?? "");
    }

    public async Task<OAuthRefresh> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var (status, body) = await SendAsync(Client, TokenRequest(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }), "Microsoft", ct);
        if (status == HttpStatusCode.BadRequest && Dead.Contains(Str(body, "error"))) return new OAuthRefresh(null, Revoked: true);
        var token = Str(body, "access_token");
        // Any other refusal is not proof the grant is gone, so it stays retryable rather than forcing a reconnect.
        if ((int)status is < 200 or > 299 || token is null)
            throw new HttpRequestException($"Microsoft token refresh answered HTTP {(int)status}.", null, status);
        return new OAuthRefresh(token, Revoked: false, Str(body, "refresh_token"));
    }

    private HttpClient Client => http.CreateClient(HttpClientName);

    private HttpRequestMessage TokenRequest(Dictionary<string, string> form)
    {
        form["client_id"] = options.Value.ClientId;
        form["client_secret"] = options.Value.ClientSecret;
        form["scope"] = profile.Scopes;
        return new HttpRequestMessage(HttpMethod.Post, Authority + "/token") { Content = new FormUrlEncodedContent(form) };
    }
}
