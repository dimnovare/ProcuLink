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
    /// Returns <c>true</c> when the token's <paramref name="azp"/> is present and matches one of
    /// the configured <paramref name="authorizedParties"/>.
    /// </summary>
    /// <remarks>
    /// A present <c>azp</c> is REQUIRED: production Clerk tokens always carry <c>azp</c> (this app's
    /// frontend origin), so a missing/empty <c>azp</c> is rejected rather than waved through — that
    /// allowance would let a token minted for a different app on the same Clerk instance be accepted
    /// if it omitted the claim. A present <c>azp</c> must match one of the configured
    /// <paramref name="authorizedParties"/> (compared with the set's own comparer; configure it
    /// case-insensitive).
    /// </remarks>
    public static bool IsAuthorizedParty(string? azp, IReadOnlySet<string> authorizedParties)
        => !string.IsNullOrEmpty(azp) && authorizedParties.Contains(azp);
}
