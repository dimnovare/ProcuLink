using System.Security.Claims;

namespace ProcuLink.Api.Auth;

public static class ClerkClaims
{
    private static readonly string[] EmailClaimTypes = { "email", "email_address", ClaimTypes.Email };

    /// <summary>The first non-blank email claim (Clerk surfaces it under different types per JWT template), or null.</summary>
    public static string? Email(ClaimsPrincipal user) =>
        EmailClaimTypes.Select(t => user.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
