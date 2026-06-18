using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

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
///   • FAIL CLOSED: an empty/unset allowlist authorises NO ONE → 403;
///   • FAIL CLOSED: a missing <see cref="AdminAllowlist"/> registration → 403.
///
/// <para>
/// <b>Exact identity inputs.</b> The caller's Clerk user id is read from the
/// <c>"sub"</c> claim. The caller's email is read from the FIRST present of these
/// claim types, in order (Clerk JWT templates vary by configuration):
/// <list type="bullet">
///   <item><description><c>"email"</c></description></item>
///   <item><description><c>"email_address"</c></description></item>
///   <item><description><see cref="ClaimTypes.Email"/>
///     (<c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress</c>)</description></item>
/// </list>
/// Either an allowlisted <c>sub</c> OR an allowlisted email grants access; matching is
/// case-insensitive and whitespace-trimmed (see <see cref="AdminAllowlist.IsAdmin"/>).
/// </para>
///
/// <para>
/// <b>Diagnostics.</b> Every FAIL-CLOSED 403 (non-allowlisted principal, or a missing
/// <see cref="AdminAllowlist"/> service) is logged as an <see cref="LogLevel.Warning"/> via
/// the <c>ProcuLink.Api.Auth.AdminOnlyAttribute</c> category. The log includes the presented
/// <c>sub</c> and email (these are admin-identity values being matched against the allowlist —
/// not secrets) so a misconfigured allowlist is diagnosable from the rejection itself. The
/// 401 (unauthenticated) path is not logged here — there is no presented admin identity to record.
/// </para>
///
/// The dev-only QA-bypass principal ("user_qa_local") carries no email and a
/// fixed sub, so it is rejected unless that exact id is explicitly allowlisted.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    // Clerk session tokens may surface the email under any of these claim types,
    // depending on the JWT template. We check all of them, in this order, and use
    // the first non-blank value (see EmailClaimTypes usage in OnAuthorization).
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
            // 401, not a fail-closed admin rejection: there is no presented admin
            // identity to record, so nothing useful to log here.
            context.Result = new UnauthorizedResult();
            return;
        }

        var sub   = user.FindFirst("sub")?.Value;
        var email = EmailClaimTypes
            .Select(t => user.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var allowlist = context.HttpContext.RequestServices.GetService(typeof(AdminAllowlist)) as AdminAllowlist;
        if (allowlist is null)
        {
            // Misconfiguration — fail closed rather than open. Log the presented claims
            // so an operator can see this was a config gap (allowlist not registered),
            // not a genuinely unauthorised user.
            LogFailClosed(context, "AdminAllowlist service not registered", sub, email, allowlistConfigured: null);
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        if (!allowlist.IsAdmin(sub, email))
        {
            // FAIL-CLOSED 403. Log the presented sub/email (admin-identity values matched
            // against the allowlist — not secrets) plus whether the allowlist is configured
            // at all, so a misconfigured allowlist (e.g. empty Admin:Emails) is diagnosable
            // from the rejection itself.
            LogFailClosed(context, "presented principal is not in the admin allowlist",
                sub, email, allowlistConfigured: allowlist.IsConfigured);
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// Emits one structured <see cref="LogLevel.Warning"/> describing a fail-closed admin
    /// rejection. Best-effort: if no <see cref="ILoggerFactory"/> is registered (e.g. a unit
    /// test that builds a bare service provider) this is a no-op and never throws — the auth
    /// decision must not depend on logging being available.
    /// </summary>
    private static void LogFailClosed(
        AuthorizationFilterContext context, string reason, string? sub, string? email, bool? allowlistConfigured)
    {
        try
        {
            var loggerFactory = context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            var logger = loggerFactory?.CreateLogger("ProcuLink.Api.Auth.AdminOnlyAttribute");
            logger?.LogWarning(
                "Admin gate FAIL-CLOSED (403): {Reason}. path={Path} sub={Sub} email={Email} allowlistConfigured={AllowlistConfigured}",
                reason,
                context.HttpContext.Request?.Path.Value,
                string.IsNullOrWhiteSpace(sub) ? "(none)" : sub,
                string.IsNullOrWhiteSpace(email) ? "(none)" : email,
                allowlistConfigured);
        }
        catch
        {
            // Logging must never break the authorization decision (already fail-closed above).
        }
    }
}
