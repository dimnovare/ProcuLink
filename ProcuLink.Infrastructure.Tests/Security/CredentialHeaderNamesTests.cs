using FluentAssertions;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Security;

/// <summary>
/// Which delivery-config header names are treated as credential-bearing.
///
/// <para>The whole guard turns on this predicate, and it can fail in two directions that cost
/// different things. A false NEGATIVE leaves a secret in cleartext under a name someone chose
/// obscurely. A false POSITIVE hard-blocks a legitimate save — and the delivery editor has no
/// headers field, so there is no UI workaround. Both directions are therefore asserted, and the
/// rule is deliberately precise rather than aggressive: never bare <c>auth</c>, never bare
/// <c>key</c>.</para>
/// </summary>
public class CredentialHeaderNamesTests
{
    // ── Refused ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("x-api-key")]
    [InlineData("ApiKey")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Access-Token")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("Private-Token")]
    public void KnownCredentialNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    /// <summary>The segment rule, which is what catches a bespoke supplier-specific name.</summary>
    [Theory]
    [InlineData("X-Supplier-Token")]
    [InlineData("X-Acme-Secret")]
    [InlineData("X-Client-Password")]
    [InlineData("X-Legacy-Passwd")]
    [InlineData("X-Old-Pwd")]
    [InlineData("X-Supplier-Credentials")]
    [InlineData("X-Foo-Api-Key")]
    [InlineData("X-Aws-Access-Key")]
    [InlineData("X-Signing-Key")]
    [InlineData("X_Supplier_Token")]
    public void CredentialShapedNames_AreRefused(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeTrue();

    // ── Allowed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The line the segment rule is drawn on. Every one of these is a header a real tenant sends,
    /// and refusing any of them would block a save with nowhere to go. <c>X-Idempotency-Key</c> and
    /// <c>X-Auth-Email</c> are the two that a sloppier rule (bare <c>key</c>, bare <c>auth</c>)
    /// would take out.
    /// </summary>
    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("X-Correlation-Id")]
    [InlineData("X-Request-Id")]
    [InlineData("X-Supplier-Account")]
    [InlineData("X-Idempotency-Key")]
    [InlineData("X-Partition-Key")]
    [InlineData("X-Sort-Key")]
    [InlineData("X-Auth-Email")]
    [InlineData("X-Message-Id")]
    public void OrdinaryHeaders_AreAllowed(string name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNames_AreNotCredentials(string? name) =>
        DeliveryConfigTransport.IsCredentialHeaderName(name).Should().BeFalse();

    [Fact]
    public void SurroundingWhitespace_DoesNotEvadeTheRule() =>
        DeliveryConfigTransport.IsCredentialHeaderName("  Authorization  ").Should().BeTrue();

    /// <summary>
    /// Walks the published list itself, so an entry added to it can never be added without being
    /// covered. The count floor is the anti-vacuity guard: an emptied list would otherwise make
    /// this test assert nothing at all and still pass.
    /// </summary>
    [Fact]
    public void EveryKnownCredentialHeaderName_IsRefusedByThePredicate()
    {
        var names = DeliveryConfigTransport.KnownCredentialHeaderNames;

        names.Should().HaveCountGreaterThan(10,
            "an emptied or gutted list would make this walk assert nothing");

        foreach (var name in names)
            DeliveryConfigTransport.IsCredentialHeaderName(name)
                .Should().BeTrue($"'{name}' is on the published known-credential list");
    }
}
