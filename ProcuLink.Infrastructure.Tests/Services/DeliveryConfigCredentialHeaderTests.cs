using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The cleartext invariant on a delivery config's <c>ConfigJson</c>, enforced by the write rule.
///
/// <para><b>The defect.</b> The column is cleartext by design and its doc comment says no credential
/// may ever be written into it — every secret belongs AES-GCM encrypted in
/// <c>EncryptedCredentials</c>. The HTTP channel's extra-headers map broke that in prose only: an
/// operator typing <c>Authorization: Bearer …</c> had the token stored in clear and returned by
/// GET.</para>
///
/// <para><b>Why the grandfather.</b> An editor that has no headers field carries an unmanaged
/// <c>headers</c> key through every save untouched, so a flat refusal would block an operator from
/// changing a timeout with no UI anywhere to remove the header. An identical round-trip is
/// therefore not a write; adding or rotating one is.</para>
///
/// <para>2026-11-13 (plan 13b): re-anchored from <c>DeliveryConfigService.UpsertAsync</c>, deleted
/// with the order pipeline, onto <see cref="DeliveryConfigRules.ValidateCredentialHeaders"/> — the
/// rule the flow destination save path runs — and the read-path facts onto
/// <see cref="DeliveryConfigTransport.DescribeConfigWarnings"/>. The persistence-only fact ("a
/// refused header is nowhere in the persisted column") went with the service.</para>
/// </summary>
public class DeliveryConfigCredentialHeaderTests
{
    private const string Token = "t0ps3cret";
    private static readonly string WithToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml"}}""";

    /// <summary><paramref name="stored"/> is the config already saved, or null for a first save.</summary>
    private static Action Save(string configJson, string? stored = null) =>
        () => DeliveryConfigRules.ValidateCredentialHeaders(configJson, stored);

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnAuthorizationHeader_IsRefused()
    {
        var thrown = Save(WithToken).Should().Throw<CredentialHeaderInConfigException>();

        // Code is a const (implicitly static) on the exception type, so it is qualified by type
        // rather than through the instance — the value under test is the same either way.
        CredentialHeaderInConfigException.Code.Should().Be("credential_header_in_delivery_config");
        thrown.Which.HeaderNames.Should().Equal("Authorization");
    }

    /// <summary>
    /// The refusal is asserted BEFORE the message is checked for the token. Asserting only that the
    /// message hides it would pass vacuously the moment the guard stopped refusing at all.
    /// </summary>
    [Fact]
    public void RefusalMessage_NamesTheHeaderAndNeverTheToken()
    {
        var thrown = Save(WithToken).Should().Throw<CredentialHeaderInConfigException>();

        thrown.Which.PolicyMessage.Should().Contain("'Authorization'");
        thrown.Which.PolicyMessage.Should().NotContain(Token);
        thrown.Which.Message.Should().NotContain(Token);
    }

    [Fact]
    public void AddingACredentialHeaderToAnExistingConfig_IsRefused()
    {
        Save(WithToken, stored: Clean).Should().Throw<CredentialHeaderInConfigException>()
            .Which.HeaderNames.Should().Equal("Authorization");
    }

    // ── Allowances (a rule that refused everything would pass a refusal-only suite) ──

    [Fact]
    public void OrdinaryHeaders_AreAllowed()
    {
        // X-Idempotency-Key and X-Auth-Email are the classifier's precision line, not decoration:
        // they are the two headers the segment rule would refuse if it took bare `key` or bare
        // `auth`, and a false refusal here is a save the operator cannot make and cannot work
        // around.
        Save("""{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml","X-Correlation-Id":"abc","X-Supplier-Account":"ACME-4417","X-Idempotency-Key":"9f2c","X-Auth-Email":"ops@supplier.example"}}""")
            .Should().NotThrow();
    }

    [Fact]
    public void NoHeadersAtAll_IsAllowed()
    {
        Save("""{"url":"https://supplier.example/orders","timeoutSeconds":30}""").Should().NotThrow();
    }

    // ── Grandfathering: the stored config predates enforcement ───────────────

    [Fact]
    public void AnUnchangedRoundTripOfALegacyHeader_IsAllowed()
    {
        Save(WithToken, stored: WithToken).Should().NotThrow();
    }

    /// <summary>
    /// The realistic migration case: an operator changes the timeout on a config that predates
    /// enforcement. That must not be blocked — there is no UI to remove the header.
    /// </summary>
    [Fact]
    public void AnUnrelatedEditBesideALegacyHeader_IsAllowed()
    {
        Save($$$"""{"url":"https://supplier.example/orders","timeoutSeconds":90,"headers":{"Authorization":"Bearer {{{Token}}}"}}""",
                stored: WithToken)
            .Should().NotThrow();
    }

    [Fact]
    public void RotatingALegacyHeaderValue_IsRefused()
    {
        Save("""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer rotated-value"}}""",
                stored: WithToken)
            .Should().Throw<CredentialHeaderInConfigException>()
            .Which.HeaderNames.Should().Equal("Authorization");
    }

    [Fact]
    public void RemovingALegacyHeader_IsAllowed()
    {
        Save(Clean, stored: WithToken).Should().NotThrow();
    }

    // ── The read surface ─────────────────────────────────────────────────────

    /// <summary>How an operator whose config predates enforcement finds out.</summary>
    [Fact]
    public void ALegacyCredentialHeader_IsReportedWithoutTheToken()
    {
        var warning = DeliveryConfigTransport.DescribeConfigWarnings(DeliveryProtocolConstants.Http, WithToken);

        warning.Should().NotBeNullOrWhiteSpace();
        warning.Should().Contain("'Authorization'");
        warning.Should().NotContain(Token);
    }

    [Fact]
    public void ACleanConfig_HasNoWarning()
    {
        DeliveryConfigTransport.DescribeConfigWarnings(DeliveryProtocolConstants.Http, Clean).Should().BeNull();
    }

    /// <summary>
    /// A config that is BOTH cleartext and credential-bearing reports both faults, because fixing
    /// one does not fix the other and an operator told only about the URL would leave the token in
    /// place.
    /// </summary>
    [Fact]
    public void BothFaults_AreBothReported()
    {
        // $$$ / {{{...}}}: $$ / {{...}} does not compile here (CS9007) — the literal "}}" closing
        // the headers object and then the outer object immediately follows the interpolation's own
        // closing "}}", producing a same-length brace run the 2-brace delimiter cannot disambiguate.
        var warning = DeliveryConfigTransport.DescribeConfigWarnings(DeliveryProtocolConstants.Http,
            $$$"""{"url":"http://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""");

        warning.Should().Contain("https://", "the transport fault must still be reported");
        warning.Should().Contain("'Authorization'", "the credential-header fault must be reported too");
        warning.Should().NotContain(Token);
    }
}
