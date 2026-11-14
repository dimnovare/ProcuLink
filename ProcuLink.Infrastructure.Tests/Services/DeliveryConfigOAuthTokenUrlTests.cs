using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Transport security for the OAuth token URL stored INSIDE the encrypted delivery credentials —
/// the one outbound URL the save path never looked at.
///
/// <para><b>The gap this closes.</b> <c>ValidateTransportSecurity</c> inspects the transport
/// <c>url</c> in <c>config_json</c>, but <c>tokenUrl</c> lives in the credentials blob, which was
/// encrypted with no URL inspection at all. At send time <c>HttpAuthApplier</c> runs only the SSRF
/// guard, which deliberately allows plain http. So a customer could save
/// <c>http://…/oauth/token</c> and the client-credentials exchange would POST <c>client_id</c> AND
/// <c>client_secret</c> as a cleartext form body — leaking the secret itself, not merely the
/// endpoint it protects.</para>
///
/// <para><b>Both directions.</b> Modelled on <see cref="DeliveryConfigTransportSecurityTests"/>:
/// every refusal is paired with the save it must not break — https token URLs, loopback http
/// token URLs (the local dev loop and e2e suites), credentials with no token URL at all, and
/// protocols whose credentials never carry one.</para>
///
/// <para>2026-11-13 (plan 13b): re-anchored from <c>DeliveryConfigService.UpsertAsync</c>, deleted
/// with the order pipeline, onto <see cref="DeliveryConfigRules.ValidateCredentialTokenUrl"/> — the
/// rule the flow destination save path runs. The persistence-only fact ("a refused token URL does
/// not overwrite working credentials") went with the service.</para>
/// </summary>
public class DeliveryConfigOAuthTokenUrlTests
{
    private static Action Validate(string? credentialsJson) =>
        () => DeliveryConfigRules.ValidateCredentialTokenUrl(credentialsJson);

    // ── Refusals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The verbatim defect: an https delivery endpoint with a cleartext OAuth token endpoint.
    /// The document would travel encrypted while the credentials that authenticate it travel in clear.
    /// </summary>
    [Fact]
    public void HttpTokenUrlOnANonLoopbackHost_IsRefused()
    {
        var thrown = Validate(
                """{"type":"oauth2_client_credentials","tokenUrl":"http://auth.supplier.example/oauth/token","clientId":"cid","clientSecret":"hunter2"}""")
            .Should().Throw<OutboundUrlPolicyException>();

        thrown.Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        thrown.Which.Message.Should().Contain("https://");
        thrown.Which.Message.Should().NotContain("hunter2");
    }

    /// <summary>
    /// <c>HttpAuthApplier</c> reads the token URL off a <c>JsonElement</c>, and a validator that
    /// bets on which of a repeated key's values the reader binds is a validator that can be
    /// disagreed with. Same rule as the config_json <c>url</c> walk: EVERY tokenUrl-keyed value is
    /// judged, in both orders.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"oauth2_client_credentials","tokenUrl":"https://ok.example/token","tokenUrl":"http://evil.example/token","clientId":"c","clientSecret":"s"}""")]
    [InlineData("""{"type":"oauth2_client_credentials","tokenUrl":"http://evil.example/token","tokenUrl":"https://ok.example/token","clientId":"c","clientSecret":"s"}""")]
    public void ARepeatedTokenUrlKey_CannotSmuggleACleartextEndpointPast(string credentialsJson)
    {
        Validate(credentialsJson).Should().Throw<OutboundUrlPolicyException>()
            .Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    /// <summary>
    /// A token URL with a userinfo component is the other thing the shared policy refuses: it
    /// would put a second credential in cleartext next to the one being fetched. The refusal must
    /// not echo it.
    /// </summary>
    [Fact]
    public void CredentialsEmbeddedInTheTokenUrl_AreRefusedWithoutEcho()
    {
        var thrown = Validate(
                """{"type":"oauth2_client_credentials","tokenUrl":"https://user:hunter2@auth.supplier.example/token","clientId":"c","clientSecret":"s"}""")
            .Should().Throw<OutboundUrlPolicyException>();

        thrown.Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        thrown.Which.Message.Should().NotContain("hunter2");
    }

    // ── Allowances (a rule that refused every save would pass a refusal-only suite) ──

    [Fact]
    public void HttpsTokenUrl_IsAllowed()
    {
        Validate("""{"type":"oauth2_client_credentials","tokenUrl":"https://auth.supplier.example/oauth/token","clientId":"c","clientSecret":"s"}""")
            .Should().NotThrow();
    }

    /// <summary>
    /// Mirrors <see cref="OutboundUrlPolicy"/>'s loopback exemption: the local dev loop and the
    /// e2e suites stand up loopback token listeners, and that traffic never leaves the machine.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:53412/token")]
    [InlineData("http://localhost:5223/oauth/token")]
    [InlineData("http://[::1]:9000/token")]
    public void PlainHttpTokenUrlOnLoopback_IsAllowed(string tokenUrl)
    {
        Validate($$"""{"type":"oauth2_client_credentials","tokenUrl":"{{tokenUrl}}","clientId":"c","clientSecret":"s"}""")
            .Should().NotThrow();
    }

    /// <summary>
    /// Credentials that carry no token URL — every non-OAuth auth type, and every host-based
    /// protocol's credential blob — must be untouched by this rule.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"bearer","token":"tok-abc"}""")]
    [InlineData("""{"type":"basic","username":"u","password":"p"}""")]
    [InlineData("""{"type":"apikey","header":"X-Api-Key","value":"k"}""")]
    [InlineData(null)]
    public void CredentialsWithoutATokenUrl_AreUnaffected(string? credentialsJson)
    {
        Validate(credentialsJson).Should().NotThrow();
    }

    /// <summary>
    /// A blank tokenUrl is not a network target: it saves and fails at send time with the
    /// enumerated "OAuth token URL is missing" message. Turning that into a save-time refusal
    /// would be a separate behaviour change from the one this guard is for.
    /// </summary>
    [Fact]
    public void ABlankTokenUrl_IsAllowed()
    {
        Validate("""{"type":"oauth2_client_credentials","tokenUrl":"","clientId":"c","clientSecret":"s"}""")
            .Should().NotThrow();
    }

    /// <summary>
    /// SFTP credentials never carry a tokenUrl, but a rule scoped to a protocol list goes stale in
    /// one direction — so the check is scope-free and this pins that host-based saves keep working.
    /// </summary>
    [Fact]
    public void HostBasedProtocolCredentials_AreUnaffected()
    {
        Validate("""{"password":"sftp-pass"}""").Should().NotThrow();
        FluentActions.Invoking(() => DeliveryConfigRules.ValidateTransportSecurity(
                DeliveryProtocolConstants.Sftp, "{\"host\":\"files.supplier.example\",\"port\":22,\"remotePath\":\"/in\"}"))
            .Should().NotThrow();
    }
}
