using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ProcuLink.Api.Auth;

/// <summary>
/// Development-only authentication scheme used by Playwright live QA.
/// It is registered only when the host is Development and PROCULINK_QA_BYPASS_AUTH=true.
/// </summary>
public sealed class QaBypassAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "QaBypass";

    public QaBypassAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", "user_qa_local"),
            new Claim("org_id", "org_qa_local"),
            new Claim("org_slug", "QA Local"),
            // The local QA principal is the sole member of its own throwaway org, so it is that
            // org's administrator and the admin-gated screens are reachable during QA. Stated
            // explicitly rather than inherited from a missing claim — a token with no role is
            // refused by RequireOrgAdminAttribute, and that is the behaviour worth keeping.
            // This handler is registered only when IsDevelopment() && PROCULINK_QA_BYPASS_AUTH.
            new Claim("org_role", "org:admin"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
