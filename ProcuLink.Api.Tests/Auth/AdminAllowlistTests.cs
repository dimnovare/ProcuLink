using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Auth;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

/// <summary>
/// Unit tests for the platform-admin allowlist. Security-critical: this gate is
/// the ONLY thing standing between an authenticated tenant user and the
/// cross-tenant admin surface, so its matching + fail-closed behaviour is tested
/// directly.
/// </summary>
public class AdminAllowlistTests
{
    private static AdminAllowlist Make(string? userIds = null, string? emails = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:UserIds"] = userIds,
                ["Admin:Emails"]  = emails,
            })
            .Build());

    [Fact]
    public void EmptyAllowlist_AuthorisesNoOne_FailClosed()
    {
        var allowlist = Make(userIds: null, emails: null);

        allowlist.IsConfigured.Should().BeFalse();
        allowlist.IsAdmin("user_anything", "anyone@example.com").Should().BeFalse();
        allowlist.IsAdmin(null, null).Should().BeFalse();
    }

    [Fact]
    public void BlankAllowlist_AuthorisesNoOne_FailClosed()
    {
        var allowlist = Make(userIds: "   ", emails: " , ");

        allowlist.IsConfigured.Should().BeFalse();
        allowlist.IsAdmin("user_x", "x@example.com").Should().BeFalse();
    }

    [Fact]
    public void UserIdInAllowlist_IsAdmin()
    {
        var allowlist = Make(userIds: "user_admin_1");

        allowlist.IsConfigured.Should().BeTrue();
        allowlist.IsAdmin("user_admin_1", email: null).Should().BeTrue();
    }

    [Fact]
    public void EmailInAllowlist_IsAdmin()
    {
        var allowlist = Make(emails: "founder@proculink.eu");

        allowlist.IsAdmin(clerkUserId: null, "founder@proculink.eu").Should().BeTrue();
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var allowlist = Make(userIds: "User_Admin_1", emails: "Founder@ProcuLink.EU");

        allowlist.IsAdmin("user_admin_1", null).Should().BeTrue("user-id match must be case-insensitive");
        allowlist.IsAdmin(null, "founder@proculink.eu").Should().BeTrue("email match must be case-insensitive");
    }

    [Fact]
    public void MatchingTrimsWhitespace_OnBothConfigAndInput()
    {
        var allowlist = Make(userIds: "  user_admin_1  ,  user_admin_2 ", emails: " redacted@example.invalid ");

        allowlist.IsAdmin("user_admin_2", null).Should().BeTrue("config entries are trimmed");
        allowlist.IsAdmin("  user_admin_1  ", null).Should().BeTrue("the incoming claim value is trimmed too");
        allowlist.IsAdmin(null, "  c@d.example ").Should().BeTrue();
    }

    [Fact]
    public void NonMatchingPrincipal_IsNotAdmin()
    {
        var allowlist = Make(userIds: "user_admin_1", emails: "founder@proculink.eu");

        allowlist.IsAdmin("user_random", "random@example.com").Should().BeFalse();
    }

    [Fact]
    public void EitherMatch_Grants_Access()
    {
        var allowlist = Make(userIds: "user_admin_1", emails: "founder@proculink.eu");

        // sub matches, email doesn't
        allowlist.IsAdmin("user_admin_1", "not-admin@example.com").Should().BeTrue();
        // email matches, sub doesn't
        allowlist.IsAdmin("user_not_admin", "founder@proculink.eu").Should().BeTrue();
    }
}
