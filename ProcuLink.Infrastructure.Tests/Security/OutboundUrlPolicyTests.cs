using System.Linq;
using FluentAssertions;
using ProcuLink.Core.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Adversarial gate for <see cref="OutboundUrlPolicy"/> — the shared transport-security policy for
/// every tenant-supplied outbound URL (supplier delivery endpoints, ERP connector endpoints,
/// catalog pull feeds, OAuth token endpoints, webhook targets, S3-compatible ingress endpoints).
///
/// <para>Two separate weaknesses are pinned here, because a purchase order carries both a
/// commercial document and the credentials used to deliver it:</para>
/// <list type="number">
///   <item>plain <c>http://</c> to a real host ships the PO and its credentials in clear text; and</item>
///   <item>credentials embedded in the URL's userinfo are stored and re-displayed in clear text.</item>
/// </list>
///
/// <para>The tests deliberately assert BOTH directions. A policy that refused everything would
/// pass a refusal-only suite, so every refusal case is paired with the allowance it must not
/// break — https to a real host, and plain http to loopback for local development.</para>
/// </summary>
public class OutboundUrlPolicyTests
{
    // ── Refusals: plain http to a real host ──────────────────────────────────

    [Theory]
    [InlineData("http://supplier.example.com/orders")]
    [InlineData("http://erp.supplier.com/xmlcore")]
    [InlineData("HTTP://supplier.example.com/orders")]        // scheme casing must not evade
    [InlineData("http://supplier.example.com:8080/orders")]
    [InlineData("http://192.0.2.10/inbound")]                 // TEST-NET-1, a routable literal
    public void Inspect_PlainHttpToARealHost_IsRefusedAsInsecureTransport(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    [Fact]
    public void Inspect_PlainHttp_ExplainsWhatToDoRatherThanEmittingACode()
    {
        var verdict = OutboundUrlPolicy.Inspect("http://supplier.example.com/orders");

        // The operator must be told what is wrong and what to do about it. A bare
        // "url_requires_tls" in the UI is a defect, not an error message.
        verdict.Message.Should().Contain("https://");
        verdict.Message.Should().NotBe(verdict.ErrorCode);
        verdict.Message.Length.Should().BeGreaterThan(40);
    }

    /// <summary>
    /// Private/RFC-1918 and link-local ranges get NO cleartext exemption. They are already blocked
    /// outright in production by the SSRF guard (OutboundRequestGuard), so refusing them here
    /// removes nothing that works today, and "it is an internal network" is a claim ProcuLink
    /// cannot verify from a URL string.
    /// </summary>
    [Theory]
    [InlineData("http://10.0.0.5/xmlcore")]
    [InlineData("http://192.168.1.20/orders")]
    [InlineData("http://172.16.4.4/orders")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void Inspect_PlainHttpToAPrivateOrLinkLocalAddress_IsRefused(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    // ── Allowances: the rule must not be "refuse everything" ─────────────────

    [Theory]
    [InlineData("https://supplier.example.com/orders")]
    [InlineData("https://erp.supplier.com/xmlcore?db=live")]
    [InlineData("HTTPS://supplier.example.com/orders")]
    [InlineData("https://supplier.example.com:8443/orders")]
    public void Inspect_HttpsToARealHost_IsAllowed(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeTrue();
        verdict.ErrorCode.Should().BeNull();
    }

    /// <summary>
    /// Local development and the test suites drive delivery against loopback listeners. Plain
    /// http there never leaves the machine, so it stays allowed — otherwise this change would
    /// break the local dev loop it is meant to protect.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5223/api/delivery")]
    [InlineData("http://LOCALHOST:5223/api/delivery")]
    [InlineData("http://127.0.0.1:9000/inbound")]
    [InlineData("http://127.0.0.2:9000/inbound")]   // all of 127.0.0.0/8 is loopback
    [InlineData("http://[::1]:9000/inbound")]
    public void Inspect_PlainHttpOnLoopback_IsAllowedForLocalDevelopment(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeTrue();
        verdict.ErrorCode.Should().BeNull();
    }

    // ── Refusals: credentials embedded in the URL ────────────────────────────

    [Theory]
    [InlineData("https://user:pass@supplier.example/orders")]
    [InlineData("https://apikey@supplier.example/orders")]
    [InlineData("http://user:pass@localhost:5223/orders")]   // loopback is no excuse
    public void Inspect_CredentialsInTheUrl_AreRefused(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
    }

    /// <summary>
    /// The refusal message is surfaced in the UI and written to logs/Sentry. Echoing the rejected
    /// URL back would put the very password we are refusing into exactly the places the refusal
    /// exists to keep it out of.
    /// </summary>
    [Fact]
    public void Inspect_CredentialsInTheUrl_MessageNeverEchoesTheSecret()
    {
        var verdict = OutboundUrlPolicy.Inspect("https://admin:hunter2@supplier.example/orders");

        // Assert the refusal first: without this, disabling the userinfo check entirely would
        // leave Message null and the two NotContain assertions would still pass vacuously.
        verdict.Allowed.Should().BeFalse();
        verdict.Message.Should().NotBeNullOrWhiteSpace();
        verdict.Message.Should().NotContain("hunter2");
        verdict.Message.Should().NotContain("admin");
    }

    // ── Refusals: malformed and non-web schemes ──────────────────────────────

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://supplier.example/1")]
    [InlineData("ftp://supplier.example/orders")]
    public void Inspect_ANonWebScheme_IsRefused(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorSchemeNotAllowed);
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("supplier.example.com/orders")]
    [InlineData("not a url at all")]
    public void Inspect_ARelativeOrMalformedUrl_IsRefused(string url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorNotAbsolute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inspect_AMissingUrl_IsRefused(string? url)
    {
        var verdict = OutboundUrlPolicy.Inspect(url);

        verdict.Allowed.Should().BeFalse();
        verdict.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorUrlRequired);
    }

    [Fact]
    public void Inspect_SurroundingWhitespace_IsToleratedNotTreatedAsMalformed()
    {
        OutboundUrlPolicy.Inspect("  https://supplier.example/orders  ").Allowed.Should().BeTrue();
    }

    // ── The declared scheme sets are the single source of truth ──────────────

    /// <summary>
    /// The frontend mirrors these sets (src/lib/outboundUrlPolicy.ts). Both the policy and its
    /// tests read them from here rather than re-typing "https", so a change to the ladder cannot
    /// pass a suite that still asserts the old one.
    /// </summary>
    [Fact]
    public void SchemeSets_AreDeclaredOnceAndAreNotVacuous()
    {
        OutboundUrlPolicy.SecureSchemes.Should().NotBeEmpty();
        OutboundUrlPolicy.SecureSchemes.Should().Contain("https");
        OutboundUrlPolicy.LoopbackOnlySchemes.Should().Equal("http");

        // Refuted by construction: no scheme may be in both sets.
        OutboundUrlPolicy.SecureSchemes.Intersect(OutboundUrlPolicy.LoopbackOnlySchemes)
            .Should().BeEmpty();
    }

    [Fact]
    public void EverySecureScheme_IsAllowedToARealHost()
    {
        foreach (var scheme in OutboundUrlPolicy.SecureSchemes)
            OutboundUrlPolicy.Inspect($"{scheme}://supplier.example/orders").Allowed
                .Should().BeTrue($"'{scheme}' is declared secure");
    }

    [Fact]
    public void EveryLoopbackOnlyScheme_IsRefusedToARealHostAndAllowedOnLoopback()
    {
        foreach (var scheme in OutboundUrlPolicy.LoopbackOnlySchemes)
        {
            OutboundUrlPolicy.Inspect($"{scheme}://supplier.example/orders").Allowed
                .Should().BeFalse($"'{scheme}' is loopback-only");
            OutboundUrlPolicy.Inspect($"{scheme}://127.0.0.1:9000/orders").Allowed
                .Should().BeTrue($"'{scheme}' is allowed on loopback");
        }
    }

    // ── The subject noun makes one shared policy readable at every call site ──

    [Fact]
    public void Inspect_UsesTheCallersSubjectNounInTheMessage()
    {
        var verdict = OutboundUrlPolicy.Inspect("http://supplier.example/orders", "Delivery endpoint");

        verdict.Message.Should().StartWith("Delivery endpoint");
    }
}
