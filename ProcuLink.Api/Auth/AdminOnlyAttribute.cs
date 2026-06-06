using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcuLink.Api.Auth;

/// <summary>
/// Authorization filter that admits ONLY platform admins listed in the
/// <see cref="AdminAllowlist"/> (config keys <c>Admin:UserIds</c> / <c>Admin:Emails</c>,
/// env <c>Admin__UserIds</c> / <c>Admin__Emails</c>).
///
/// Applied to <c>AdminController</c> — the single deliberately cross-tenant
/// surface — so its gate must be airtight:
///   • caller must be authenticated (else 401);
///   • the JWT "sub" must be in <c>Admin:UserIds</c> OR an email claim must be
///     in <c>Admin:Emails</c> (case-insensitive, trimmed) — else 403;
///   • FAIL CLOSED: an empty/unset allowlist authorises NO ONE → 403.
///
/// The dev-only QA-bypass principal ("user_qa_local") carries no email and a
/// fixed sub, so it is rejected unless that exact id is explicitly allowlisted.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    // Clerk session tokens may surface the email under any of these claim types,
    // depending on the JWT template. We check all of them.
    private static readonly string[] EmailClaimTypes =
    {
        "email",
        "email_address",
        ClaimTypes.Email,
    };

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var allowlist = context.HttpContext.RequestServices.GetService(typeof(AdminAllowlist)) as AdminAllowlist;
        if (allowlist is null)
        {
            // Misconfiguration — fail closed rather than open.
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        var sub   = user.FindFirst("sub")?.Value;
        var email = EmailClaimTypes
            .Select(t => user.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (!allowlist.IsAdmin(sub, email))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }
}
