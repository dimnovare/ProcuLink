namespace ProcuLink.Core.Services.Google;

public sealed record GoogleGrant(string AccessToken, string? RefreshToken, string Scope);

public sealed record GoogleUser(string Subject, string Email);

/// <summary><c>Revoked</c>: Google answered <c>invalid_grant</c>, so the refresh token is dead and <c>AccessToken</c> is null.</summary>
public sealed record GoogleRefresh(string? AccessToken, bool Revoked);

/// <summary>A code exchange or userinfo call failed. <c>Code</c> is Google's <c>error</c> value, or <c>google_unavailable</c>; never a token.</summary>
public sealed class GoogleAuthException(string code) : Exception(code)
{
    public const string Unavailable = "google_unavailable";
    public string Code { get; } = code;
}

/// <summary>Google OAuth over plain HTTP (R4): authorize URL, code exchange, userinfo, refresh and revoke.</summary>
public interface IGoogleOAuthClient
{
    string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge);

    /// <exception cref="GoogleAuthException">Any failure, including a network one.</exception>
    Task<GoogleGrant> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct);

    /// <exception cref="GoogleAuthException">Any failure, including a network one.</exception>
    Task<GoogleUser> GetUserAsync(string accessToken, CancellationToken ct);

    /// <exception cref="HttpRequestException">Network, timeout, 5xx, or any refusal other than <c>invalid_grant</c>.</exception>
    Task<GoogleRefresh> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>Best effort; never throws.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct);
}
