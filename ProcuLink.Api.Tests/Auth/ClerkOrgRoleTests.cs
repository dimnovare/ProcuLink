using System.Security.Claims;
using FluentAssertions;
using ProcuLink.Api.Auth;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

/// <summary>
/// The claim reader, at the boundary where a Clerk token becomes an authorization decision.
///
/// <para>Both Clerk token shapes are exercised because this repo already handles both for the org
/// <i>id</i>: <c>TenantResolutionMiddleware</c> reads a flat <c>org_id</c> and falls back to the v2
/// compact <c>o</c> object. Reading the role from only one of them would work in whichever
/// environment happened to be tested and fail in the other.</para>
///
/// <para>Every "cannot tell" case below asserts <see cref="OrgRole.Unknown"/>, which
/// <see cref="RequireOrgAdminAttribute"/> refuses. That is the point: this repo has six recorded
/// instances of an unrecognised value falling through to success, and a role parser is exactly where
/// the seventh would be written.</para>
/// </summary>
public sealed class ClerkOrgRoleTests
{
    private static ClaimsPrincipal With(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    // ── Clerk v1: the flat org_role claim ─────────────────────────────────────

    [Theory]
    [InlineData("org:admin")]
    [InlineData("admin")]
    [InlineData("ORG:ADMIN")]
    [InlineData("  org:admin  ")]
    public void V1_AdminRole_IsAdmin(string raw) =>
        ClerkOrgRole.FromClaims(With(new Claim("org_role", raw))).Should().Be(OrgRole.Admin);

    [Theory]
    [InlineData("org:member")]
    [InlineData("member")]
    [InlineData("basic_member")]
    public void V1_MemberRole_IsMember(string raw) =>
        ClerkOrgRole.FromClaims(With(new Claim("org_role", raw))).Should().Be(OrgRole.Member);

    /// <summary>
    /// A Clerk CUSTOM role is a role we can read but were never told grants administration. It
    /// resolves to <see cref="OrgRole.Member"/> — refused — because this packet ships an
    /// admin/not-admin split rather than a permission matrix, and inheriting admin rights from an
    /// unrecognised name is how a narrow gate silently becomes no gate.
    /// </summary>
    [Theory]
    [InlineData("org:billing_manager")]
    [InlineData("org:supplier_editor")]
    [InlineData("something_nobody_defined")]
    public void V1_UnrecognisedCustomRole_IsNotAdmin(string raw) =>
        ClerkOrgRole.FromClaims(With(new Claim("org_role", raw))).Should().Be(OrgRole.Member);

    // ── Clerk v2: the compact "o" object claim ────────────────────────────────

    [Fact]
    public void V2_CompactOrgClaim_AdminRole_IsAdmin() =>
        ClerkOrgRole.FromClaims(With(new Claim("o", """{"id":"org_abc","rol":"admin","slg":"acme"}""")))
            .Should().Be(OrgRole.Admin);

    [Fact]
    public void V2_CompactOrgClaim_MemberRole_IsMember() =>
        ClerkOrgRole.FromClaims(With(new Claim("o", """{"id":"org_abc","rol":"member","slg":"acme"}""")))
            .Should().Be(OrgRole.Member);

    /// <summary>
    /// v1 wins when both are present. They never are on a real token, but a resolver that consulted
    /// them in an unstated order would be a coin flip if Clerk ever emitted both during a migration.
    /// </summary>
    [Fact]
    public void V1_TakesPrecedenceOverV2_WhenBothArePresent() =>
        ClerkOrgRole.FromClaims(With(
                new Claim("org_role", "org:member"),
                new Claim("o", """{"id":"org_abc","rol":"admin"}""")))
            .Should().Be(OrgRole.Member);

    // ── Everything that means "we cannot tell" ────────────────────────────────

    [Fact]
    public void NoClaimsAtAll_IsUnknown() =>
        ClerkOrgRole.FromClaims(With()).Should().Be(OrgRole.Unknown);

    [Fact]
    public void NullPrincipal_IsUnknown() =>
        ClerkOrgRole.FromClaims(null).Should().Be(OrgRole.Unknown);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankRole_IsUnknown(string raw) =>
        ClerkOrgRole.FromClaims(With(new Claim("org_role", raw))).Should().Be(OrgRole.Unknown);

    /// <summary>
    /// An organisation claim with no <c>rol</c> is the shape a Clerk JWT template produces when it
    /// emits the org but not the role. It must read as Unknown — the caller is then refused, and the
    /// gate logs that specific diagnosis so the template gap is visible from the rejection.
    /// </summary>
    [Fact]
    public void V2_CompactOrgClaim_WithoutARole_IsUnknown() =>
        ClerkOrgRole.FromClaims(With(new Claim("o", """{"id":"org_abc","slg":"acme"}""")))
            .Should().Be(OrgRole.Unknown);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"org_abc\",")]
    [InlineData("[\"an\",\"array\"]")]
    [InlineData("\"a bare string\"")]
    [InlineData("null")]
    public void V2_MalformedOrNonObjectOrgClaim_IsUnknown(string raw) =>
        ClerkOrgRole.FromClaims(With(new Claim("o", raw))).Should().Be(OrgRole.Unknown);

    /// <summary>A non-string <c>rol</c> states no role we can act on.</summary>
    [Fact]
    public void V2_NonStringRole_IsUnknown() =>
        ClerkOrgRole.FromClaims(With(new Claim("o", """{"id":"org_abc","rol":7}""")))
            .Should().Be(OrgRole.Unknown);

    /// <summary>
    /// The default value of the enum must be the refused one. Anything that forgets to resolve a
    /// role — a new code path, a struct left at its default — then lands on a refusal rather than on
    /// an admission.
    /// </summary>
    [Fact]
    public void TheDefaultRole_IsTheOneTheGateRefuses() =>
        default(OrgRole).Should().Be(OrgRole.Unknown);
}
