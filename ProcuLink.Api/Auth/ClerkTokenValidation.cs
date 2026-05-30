namespace ProcuLink.Api.Auth;

/// <summary>
/// Validation helpers for Clerk-issued session JWTs.
/// </summary>
/// <remarks>
/// Clerk session tokens do not carry a standard <c>aud</c> claim by default; they carry
/// <c>azp</c> (authorized party) — the origin of the frontend that requested the token.
/// Validating <c>azp</c> against this application's known frontend origins binds a token to
/// this app and prevents a token minted for a <i>different</i> application on the same Clerk
/// instance from being accepted — the gap left open by <c>ValidateAudience = false</c> alone.
/// </remarks>
public static class ClerkTokenValidation
{
    /// <summary>
    /// Returns <c>true</c> when the token's <paramref name="azp"/> is acceptable.
    /// </summary>
    /// <remarks>
    /// A missing/empty <c>azp</c> is accepted: only a token minted for another origin carries a
    /// non-matching <c>azp</c>, so its absence cannot indicate a cross-application token. A present
    /// <c>azp</c> must match one of the configured <paramref name="authorizedParties"/>
    /// (compared with the set's own comparer; configure it case-insensitive).
    /// </remarks>
    public static bool IsAuthorizedParty(string? azp, IReadOnlySet<string> authorizedParties)
        => string.IsNullOrEmpty(azp) || authorizedParties.Contains(azp);
}
