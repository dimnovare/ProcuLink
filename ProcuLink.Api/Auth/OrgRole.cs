using System.Security.Claims;
using System.Text.Json;

namespace ProcuLink.Api.Auth;

/// <summary>
/// The caller's role WITHIN the organisation their request resolved to.
///
/// <para>This is deliberately not the platform-admin concept — see
/// <see cref="AdminOnlyAttribute"/>, which admits ProcuLink staff from a server-side
/// env allowlist and is a different, stricter gate on a cross-tenant surface. This
/// enum describes a customer's own member vs. admin distinction.</para>
///
/// <para><b><see cref="Unknown"/> is the default(OrgRole) on purpose.</b> Anything that
/// forgets to resolve a role, or cannot, lands on the value that
/// <see cref="RequireOrgAdminAttribute"/> refuses. Admission requires
/// <see cref="Admin"/> specifically, never "not Member" — so a role we fail to read is
/// a refusal, not an admission.</para>
/// </summary>
public enum OrgRole
{
    /// <summary>
    /// The role could not be determined: no organisation resolved, no role claim on the
    /// token, or a malformed one. NEVER admitted to a gated action.
    /// </summary>
    Unknown = 0,

    /// <summary>An ordinary member. Admitted to everything except the gated actions.</summary>
    Member = 1,

    /// <summary>An organisation administrator. The only value a gated action admits.</summary>
    Admin = 2,
}

/// <summary>
/// Reads the caller's organisation role out of a Clerk session token.
///
/// <para><b>Where the role comes from.</b> Clerk is the identity provider and already owns
/// organisation membership, so the role is read from the token rather than mirrored into our
/// database. Two token shapes are in play, and this repo already handles both for the org
/// <i>id</i> (see <c>TenantResolutionMiddleware</c>):</para>
/// <list type="bullet">
///   <item><description><b>v1</b> — a flat <c>org_role</c> claim alongside <c>org_id</c> /
///     <c>org_slug</c>.</description></item>
///   <item><description><b>v2</b> — a compact <c>o</c> object claim,
///     <c>{ "id": "org_…", "rol": "admin", "slg": "…" }</c>. The .NET JWT handler stores an
///     object claim as its raw JSON string, which is why this parses text.</description></item>
/// </list>
///
/// <para><b>Why not the <c>memberships</c> table.</b> It is mapped and migrated but has no
/// writer anywhere in app code, there is no Clerk webhook or Backend-API client to populate it,
/// and <c>OrphanGuardTests</c> records it as inert with a keep-or-drop decision still owed
/// ("do not build on it in the meantime"). Persisting a second copy of a role Clerk already
/// owns, with nothing keeping the two in sync, would be a worse answer than reading the claim.</para>
/// </summary>
public static class ClerkOrgRole
{
    /// <summary>
    /// <c>HttpContext.Items</c> key under which <c>TenantResolutionMiddleware</c> stores the
    /// resolved <see cref="OrgRole"/>. Mirrors how the resolved organisation id is carried.
    /// </summary>
    public const string ItemsKey = "ProcuLink.OrgRole";

    /// <summary>Clerk's built-in administrator role key (v1 <c>org_role</c> / v2 <c>o.rol</c>).</summary>
    private const string ClerkAdminRole = "org:admin";

    /// <summary>
    /// Some JWT templates emit the role without Clerk's <c>org:</c> prefix, so both spellings
    /// of the ADMIN role are accepted. No other value is.
    /// </summary>
    private const string BareAdminRole = "admin";

    /// <summary>
    /// The role carried by the token, or <see cref="OrgRole.Unknown"/> when it carries none.
    ///
    /// <para>This reads the CLAIM only. It does not decide whether the caller is an admin of the
    /// organisation the request actually resolved to — that is the middleware's job, because only
    /// the middleware knows which of the tenant-resolution paths matched.</para>
    /// </summary>
    public static OrgRole FromClaims(ClaimsPrincipal? user)
    {
        if (user is null) return OrgRole.Unknown;

        // v1: flat claim.
        var raw = user.FindFirst("org_role")?.Value;

        // v2: compact "o" object claim.
        if (string.IsNullOrWhiteSpace(raw))
            raw = RoleFromCompactOrgClaim(user.FindFirst("o")?.Value);

        return Parse(raw);
    }

    /// <summary>
    /// Pulls <c>rol</c> out of the v2 compact <c>o</c> claim. Returns null for absent, malformed,
    /// or non-object JSON — every one of which means "no role was stated", not "any role".
    /// </summary>
    public static string? RoleFromCompactOrgClaim(string? oClaimJson)
    {
        if (string.IsNullOrWhiteSpace(oClaimJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(oClaimJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("rol", out var rol) && rol.ValueKind == JsonValueKind.String
                ? rol.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Malformed o claim → no role stated.
            return null;
        }
    }

    /// <summary>
    /// Maps a raw Clerk role string onto <see cref="OrgRole"/>.
    ///
    /// <para>Only an exact match on the admin role admits. A role string we can read but do not
    /// recognise — a Clerk custom role such as <c>org:billing_manager</c> — resolves to
    /// <see cref="OrgRole.Member"/>, which is refused: this packet deliberately ships an
    /// admin/not-admin split, not a permission matrix, so an unrecognised custom role must not
    /// inherit admin rights it was never granted.</para>
    /// </summary>
    public static OrgRole Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return OrgRole.Unknown;

        var value = raw.Trim();
        return value.Equals(ClerkAdminRole, StringComparison.OrdinalIgnoreCase)
            || value.Equals(BareAdminRole, StringComparison.OrdinalIgnoreCase)
                ? OrgRole.Admin
                : OrgRole.Member;
    }
}
