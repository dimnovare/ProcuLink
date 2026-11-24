namespace ProcuLink.Core.Services.Google;

public sealed record OAuthGrant(string AccessToken, string? RefreshToken, string Scope);

public sealed record OAuthUser(string Subject, string Email);

/// <summary>
/// <c>Revoked</c>: the provider refused the refresh token, so it is dead and <c>AccessToken</c> is null.
/// <c>RefreshToken</c>: a rotated one (Microsoft), which replaces the stored token.
/// </summary>
public sealed record OAuthRefresh(string? AccessToken, bool Revoked, string? RefreshToken = null);

/// <summary>A code exchange or user call failed. <c>Code</c> is the provider's <c>error</c> value, or <see cref="Unavailable"/>; never a token.</summary>
public sealed class OAuthException(string code) : Exception(code)
{
    public const string Unavailable = "unavailable";
    public string Code { get; } = code;
}

/// <summary>
/// One provider's OAuth over plain HTTP: authorize URL, code exchange, user and refresh. No revoke, on
/// purpose: Google's revoke ends the whole grant for that user, which would silently disconnect the same
/// user's account in another organisation, and Microsoft has no revoke endpoint.
/// </summary>
public interface ILinkedAccountOAuth
{
    /// <summary>The <c>LinkedAccount.Provider</c> value this client serves.</summary>
    string Provider { get; }

    /// <summary>Client id and secret are set; false keeps the provider invisible.</summary>
    bool IsConfigured { get; }

    string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge);

    /// <exception cref="OAuthException">Any failure, including a network one.</exception>
    Task<OAuthGrant> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct);

    /// <exception cref="OAuthException">Any failure, including a network one.</exception>
    Task<OAuthUser> GetUserAsync(string accessToken, CancellationToken ct);

    /// <exception cref="HttpRequestException">Network, timeout, 5xx, or any refusal that does not prove the grant is gone.</exception>
    Task<OAuthRefresh> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>The granted scope string carries what every use of this provider needs; without it nothing is saved.</summary>
    bool HasRequiredScope(string grantedScope);
}
