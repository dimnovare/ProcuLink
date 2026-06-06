using Microsoft.Extensions.Configuration;

namespace ProcuLink.Api.Auth;

/// <summary>
/// Server-side platform-admin allowlist. The owner/admin surface
/// (<c>AdminController</c>) is the ONE deliberately cross-tenant area of the
/// API, so admission is decided here and nowhere else.
///
/// Config keys (also bindable as the environment variables in brackets):
///   <c>Admin:UserIds</c>  [<c>Admin__UserIds</c>]  — comma-separated Clerk user ids (the JWT "sub" claim).
///   <c>Admin:Emails</c>   [<c>Admin__Emails</c>]   — comma-separated admin email addresses.
///
/// FAIL CLOSED: if BOTH lists are empty/unset, no principal is an admin.
/// Matching is case-insensitive and whitespace-trimmed for both keys.
/// </summary>
public sealed class AdminAllowlist
{
    private readonly HashSet<string> _userIds;
    private readonly HashSet<string> _emails;

    public AdminAllowlist(IConfiguration configuration)
    {
        _userIds = Parse(configuration["Admin:UserIds"]);
        _emails  = Parse(configuration["Admin:Emails"]);
    }

    /// <summary>True when at least one of the two allowlists has an entry.</summary>
    public bool IsConfigured => _userIds.Count > 0 || _emails.Count > 0;

    /// <summary>
    /// Authorises a caller by their Clerk user id ("sub") OR their email claim.
    /// Either match grants access. Both inputs are optional — a null/blank value
    /// simply cannot match. Returns false when the allowlist is unconfigured.
    /// </summary>
    public bool IsAdmin(string? clerkUserId, string? email)
    {
        if (!string.IsNullOrWhiteSpace(clerkUserId) && _userIds.Contains(clerkUserId.Trim()))
            return true;

        if (!string.IsNullOrWhiteSpace(email) && _emails.Contains(email.Trim()))
            return true;

        return false;
    }

    private static HashSet<string> Parse(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);

        return set;
    }
}
