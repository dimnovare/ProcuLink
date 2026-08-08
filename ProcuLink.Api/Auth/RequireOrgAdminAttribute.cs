using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcuLink.Api.Auth;

/// <summary>
/// Admits ONLY an administrator of the calling organisation. Applied to the handful of actions
/// that are irreversible, financial, or able to silently change where an organisation's documents
/// go — everything else stays open to every member on purpose.
///
/// <para><b>This is not the platform-admin gate.</b> <see cref="AdminOnlyAttribute"/> admits
/// ProcuLink staff from a server-side env allowlist onto a cross-tenant surface. This one is about
/// a customer's own members, and the two never substitute for each other.</para>
///
/// <para><b>Fail closed, in both of the ways it can fail.</b> An unauthenticated caller gets 401.
/// Every other outcome that is not provably <see cref="OrgRole.Admin"/> — an ordinary member, a
/// token with no role claim, an API key (which carries no role at all), a request whose tenant
/// never resolved — gets 403. Admission tests for <see cref="OrgRole.Admin"/> specifically; it
/// never tests for "not a member", so a role we could not read is a refusal.</para>
///
/// <para><b>Diagnostics.</b> Every refusal is logged at <c>Warning</c> under the
/// <c>ProcuLink.Api.Auth.RequireOrgAdminAttribute</c> category with the resolved role and whether
/// the token carried an organisation at all. That distinction is the one an operator needs: a
/// refusal with an organisation present but no role means the Clerk JWT template is not emitting
/// <c>org_role</c> / <c>o.rol</c>, which is a configuration fault rather than an unauthorised
/// user — and it must be diagnosable from the rejection itself rather than by guessing.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireOrgAdminAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>
    /// Machine-readable refusal code in the 403 body. The frontend branches on this, so it is a
    /// constant here rather than a literal typed at each site.
    /// </summary>
    public const string ErrorCode = "requires_org_admin";

    /// <summary>
    /// Human-readable half of the 403. Names what is needed and who can grant it, because the
    /// caller's own next step is to ask an administrator rather than to retry.
    /// </summary>
    public const string Message =
        "This action is restricted to organisation administrators. Ask an administrator in your "
      + "organisation to make this change.";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            // 401, not 403: there is no principal to judge, so this is not a role refusal.
            context.Result = new UnauthorizedResult();
            return;
        }

        var role = context.HttpContext.Items[ClerkOrgRole.ItemsKey] as OrgRole? ?? OrgRole.Unknown;

        if (role == OrgRole.Admin) return;

        LogRefusal(context, role);

        context.Result = new ObjectResult(new { error = ErrorCode, message = Message })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    /// <summary>
    /// One structured warning per refusal. Best-effort: a missing <see cref="ILoggerFactory"/> must
    /// never turn an authorization decision into an exception, so this swallows and moves on — the
    /// refusal above has already been recorded on the context.
    /// </summary>
    private static void LogRefusal(AuthorizationFilterContext context, OrgRole role)
    {
        try
        {
            var loggerFactory = context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            var logger = loggerFactory?.CreateLogger("ProcuLink.Api.Auth.RequireOrgAdminAttribute");

            // Told apart on purpose. "You are a member" is a correct refusal and needs no action.
            // "An organisation resolved but its token stated no role" is almost always a Clerk JWT
            // template that does not emit org_role / o.rol — a configuration fault that would
            // otherwise present as every administrator in that org mysteriously losing access, and
            // it must be readable off the rejection rather than guessed at.
            var stamped = context.HttpContext.Items.ContainsKey(ClerkOrgRole.ItemsKey);

            var diagnosis = (role, stamped) switch
            {
                (OrgRole.Member, _) => "caller is a member of this organisation, not an administrator",
                (_, true) => "an organisation resolved for this request but its token stated no role — "
                           + "check that the Clerk JWT template emits org_role (v1) or o.rol (v2)",
                (_, false) => "no organisation resolved for this request: either the tenant could not "
                            + "be resolved, or the caller authenticated with an API key, which "
                            + "deliberately carries no role",
            };

            logger?.LogWarning(
                "Org-admin gate FAIL-CLOSED (403): {Diagnosis}. path={Path} role={Role} sub={Sub}",
                diagnosis,
                context.HttpContext.Request?.Path.Value,
                role,
                context.HttpContext.User.FindFirst("sub")?.Value ?? "(none)");
        }
        catch
        {
            // Logging must never break the authorization decision (already fail-closed above).
        }
    }
}
