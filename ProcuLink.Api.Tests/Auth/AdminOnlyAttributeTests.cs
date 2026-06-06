using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Auth;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

/// <summary>
/// Tests the [AdminOnly] authorization filter end-to-end against constructed
/// principals. This is the gate guarding the cross-tenant admin surface, so the
/// 401 (unauthenticated), 403 (authenticated non-admin / fail-closed), and
/// 200/allowed (sub-match, email-match) outcomes are all asserted.
/// </summary>
public class AdminOnlyAttributeTests
{
    private static AuthorizationFilterContext BuildContext(
        ClaimsPrincipal user,
        string? adminUserIds = null,
        string? adminEmails = null,
        bool registerAllowlist = true)
    {
        var services = new ServiceCollection();
        if (registerAllowlist)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admin:UserIds"] = adminUserIds,
                    ["Admin:Emails"]  = adminEmails,
                })
                .Build();
            services.AddSingleton(new AdminAllowlist(config));
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User            = user,
        };

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    // ── 401 / unauthenticated ─────────────────────────────────────────────

    [Fact]
    public void Unauthenticated_Returns401()
    {
        var ctx = BuildContext(Anonymous(), adminUserIds: "user_admin");
        new AdminOnlyAttribute().OnAuthorization(ctx);
        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    // ── 403 / authenticated non-admin ─────────────────────────────────────

    [Fact]
    public void AuthenticatedNonAdmin_Returns403()
    {
        var user = Authenticated(
            new Claim("sub", "user_tenant_member"),
            new Claim("email", "redacted@example.invalid"));
        var ctx = BuildContext(user, adminUserIds: "user_admin_1", adminEmails: "founder@proculink.eu");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void EmptyAllowlist_FailsClosed_Returns403_EvenForAnySub()
    {
        var user = Authenticated(new Claim("sub", "user_anything"));
        var ctx = BuildContext(user, adminUserIds: null, adminEmails: null);

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void QaBypassDevUser_IsNotAdmin_UnlessAllowlisted_Returns403()
    {
        // The dev QA-bypass principal: sub "user_qa_local", no email.
        var user = Authenticated(new Claim("sub", "user_qa_local"));
        var ctx = BuildContext(user, adminUserIds: "user_admin_1", adminEmails: "founder@proculink.eu");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void AllowlistServiceMissing_FailsClosed_Returns403()
    {
        var user = Authenticated(new Claim("sub", "user_admin_1"));
        var ctx = BuildContext(user, registerAllowlist: false);

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── allowed / admin ───────────────────────────────────────────────────

    [Fact]
    public void AdminBySub_IsAllowed_NoResultSet()
    {
        var user = Authenticated(new Claim("sub", "user_admin_1"));
        var ctx = BuildContext(user, adminUserIds: "user_admin_1");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeNull("an admin sub must pass the gate (filter leaves Result unset)");
    }

    [Fact]
    public void AdminByEmail_IsAllowed_NoResultSet()
    {
        var user = Authenticated(
            new Claim("sub", "user_not_in_id_list"),
            new Claim("email", "founder@proculink.eu"));
        var ctx = BuildContext(user, adminUserIds: "someone_else", adminEmails: "founder@proculink.eu");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeNull("an allowlisted email must pass the gate");
    }

    [Fact]
    public void AdminByEmail_AlternateClaimType_IsAllowed()
    {
        // Clerk JWT templates may surface email under "email_address".
        var user = Authenticated(
            new Claim("sub", "user_x"),
            new Claim("email_address", "founder@proculink.eu"));
        var ctx = BuildContext(user, adminEmails: "founder@proculink.eu");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public void AdminMatch_IsCaseInsensitiveAndTrimmed()
    {
        var user = Authenticated(new Claim("sub", "  USER_Admin_1  "));
        var ctx = BuildContext(user, adminUserIds: "user_admin_1");

        new AdminOnlyAttribute().OnAuthorization(ctx);

        ctx.Result.Should().BeNull();
    }

    // ── the gate is actually wired to the controller ──────────────────────

    [Fact]
    public void AdminController_IsDecoratedWith_AdminOnly()
    {
        var attr = typeof(ProcuLink.Api.Controllers.AdminController)
            .GetCustomAttributes(typeof(AdminOnlyAttribute), inherit: true);
        attr.Should().NotBeEmpty("the cross-tenant AdminController MUST carry the [AdminOnly] gate");
    }
}
