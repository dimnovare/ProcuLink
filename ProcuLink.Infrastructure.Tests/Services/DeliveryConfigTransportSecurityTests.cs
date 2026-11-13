using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Transport security at the delivery-config WRITE rule — the check that decides whether a saved
/// endpoint may receive a document and the credentials that authenticate it.
///
/// <para>Before this rule existed, the save path never looked at the URL at all: it checked only
/// that the blob was non-empty and parseable. An operator could save
/// <c>http://erp.supplier.com/xmlcore</c> for Directo and every send would leave with
/// <c>user</c>, <c>password</c> and <c>key</c> as a cleartext form body, or save
/// <c>https://user:pass@supplier.example/orders</c> and have that password stored in
/// <c>config_json</c> as clear text.</para>
///
/// <para>Both directions are asserted. A rule that refused every URL would satisfy a
/// refusal-only suite, so each refusal is paired with the save it must not break.</para>
///
/// <para>2026-11-13 (plan 13b): re-anchored from <c>DeliveryConfigService.UpsertAsync</c>, deleted
/// with the order pipeline, onto <see cref="DeliveryConfigRules"/> — the rule the flow destination
/// save path runs. The persistence-only fact ("a refused URL does not overwrite a working config")
/// went with the service.</para>
/// </summary>
public class DeliveryConfigTransportSecurityTests
{
    private static Action Validate(string protocol, string configJson) =>
        () => DeliveryConfigRules.ValidateTransportSecurity(protocol, configJson);

    // ── Refusals ─────────────────────────────────────────────────────────────

    /// <summary>The verbatim U-3 case: Directo over cleartext ships the document plus user/password/key.</summary>
    [Fact]
    public void DirectoOverPlainHttp_IsRefused()
    {
        var thrown = Validate(DeliveryProtocolConstants.ErpDirecto,
                "{\"url\":\"http://erp.supplier.com/xmlcore\",\"database\":\"live\"}")
            .Should().Throw<OutboundUrlPolicyException>();

        thrown.Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        thrown.Which.Message.Should().Contain("https://");
    }

    [Theory]
    [InlineData(DeliveryProtocolConstants.Http)]
    [InlineData(DeliveryProtocolConstants.ErpErply)]
    [InlineData(DeliveryProtocolConstants.ErpDirecto)]
    public void AnyUrlBearingProtocolOverPlainHttp_IsRefused(string protocol)
    {
        Validate(protocol, "{\"url\":\"http://supplier.example.com/orders\",\"database\":\"live\"}")
            .Should().Throw<OutboundUrlPolicyException>()
            .Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    /// <summary>
    /// The dispatchers deserialize config JSON with <c>PropertyNameCaseInsensitive = true</c>, so
    /// <c>{"URL":...}</c> binds and is sent. A validator that only looked for the exact lowercase
    /// key would be bypassed by changing one character.
    /// </summary>
    [Theory]
    [InlineData("URL")]
    [InlineData("Url")]
    [InlineData("uRl")]
    public void UrlKeyInAnyCasing_IsStillInspected(string key)
    {
        Validate(DeliveryProtocolConstants.Http, $"{{\"{key}\":\"http://supplier.example.com/orders\"}}")
            .Should().Throw<OutboundUrlPolicyException>()
            .Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    /// <summary>
    /// A repeated <c>url</c> key defeated the inspection entirely.
    ///
    /// <para>System.Text.Json keeps BOTH properties: <c>JsonDocument.EnumerateObject</c> lists them
    /// in document order, while <c>JsonSerializer.Deserialize&lt;HttpConfig&gt;</c> — what the
    /// dispatchers use — binds the LAST. The extraction returned the first, so
    /// <c>{"url":"https://ok…","url":"http://evil…"}</c> was validated as the https endpoint and
    /// delivered to the cleartext one. The extraction now inspects EVERY url-keyed value rather than
    /// betting on which one the deserializer picks, so the two cannot disagree in either direction.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""{"url":"https://ok.example/orders","url":"http://evil.example/orders"}""")]
    [InlineData("""{"url":"http://evil.example/orders","url":"https://ok.example/orders"}""")]
    [InlineData("""{"URL":"https://ok.example/orders","url":"http://evil.example/orders"}""")]
    public void ARepeatedUrlKey_CannotSmuggleACleartextEndpointPast(string configJson)
    {
        Validate(DeliveryProtocolConstants.Http, configJson)
            .Should().Throw<OutboundUrlPolicyException>()
            .Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    /// <summary>
    /// The negative control for the case above: a blob whose repeated key is https BOTH times is
    /// unambiguous and must still save. Refusing every duplicate outright would pass the test above
    /// while turning a bypass into a different behaviour change.
    /// </summary>
    [Fact]
    public void ARepeatedUrlKeyThatIsSecureEveryTime_IsAllowed()
    {
        Validate(DeliveryProtocolConstants.Http,
                """{"url":"https://a.example/orders","url":"https://b.example/orders"}""")
            .Should().NotThrow();
    }

    /// <summary>
    /// config_json is cleartext by design (SupplierDeliveryConfig.cs documents the invariant).
    /// A userinfo URL puts a password straight into it.
    /// </summary>
    [Fact]
    public void CredentialsEmbeddedInTheDeliveryUrl_AreRefused()
    {
        var thrown = Validate(DeliveryProtocolConstants.Http,
                "{\"url\":\"https://apiuser:hunter2@supplier.example/orders\"}")
            .Should().Throw<OutboundUrlPolicyException>();

        thrown.Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        thrown.Which.Message.Should().NotContain("hunter2");
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DeliveryProtocolConstants.Http)]
    [InlineData(DeliveryProtocolConstants.ErpErply)]
    [InlineData(DeliveryProtocolConstants.ErpDirecto)]
    public void HttpsEndpoint_IsAllowed(string protocol)
    {
        Validate(protocol, "{\"url\":\"https://supplier.example/orders\",\"database\":\"live\"}")
            .Should().NotThrow();
    }

    /// <summary>
    /// The local dev loop and the e2e suites stand up a listener on an ephemeral loopback port and
    /// save <c>http://127.0.0.1:PORT/...</c> as a target. The exemption therefore has to match on
    /// host, not on an authority string with a fixed port.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:53412/reject")]
    [InlineData("http://localhost:5223/api/delivery")]
    [InlineData("http://[::1]:9000/inbound")]
    public void PlainHttpOnLoopback_IsAllowedSoLocalDevelopmentKeepsWorking(string url)
    {
        Validate(DeliveryProtocolConstants.Http, $"{{\"url\":\"{url}\"}}").Should().NotThrow();
    }

    /// <summary>
    /// Host-based channels carry no <c>url</c>. SFTP is encrypted by construction and FTPS forces
    /// explicit TLS, so the URL policy must not reach in and refuse them for lacking a scheme.
    /// </summary>
    [Theory]
    [InlineData(DeliveryProtocolConstants.Sftp)]
    [InlineData(DeliveryProtocolConstants.Ftps)]
    [InlineData(DeliveryProtocolConstants.Ftp)]
    [InlineData(DeliveryProtocolConstants.Email)]
    public void HostBasedProtocols_AreUnaffected(string protocol)
    {
        Validate(protocol, "{\"host\":\"files.supplier.example\",\"port\":22,\"remotePath\":\"/in\"}")
            .Should().NotThrow();
    }

    /// <summary>
    /// REGRESSION GUARD. <c>dtdSystemId</c> is a cXML DOCTYPE SYSTEM identifier — an XML identity
    /// string emitted into the output document and never fetched. The canonical value published by
    /// cXML is <c>http://xml.cxml.org/...</c>. Only url-keyed values are network targets, so an
    /// http DTD beside an https endpoint must not be refused: forcing https on it would emit
    /// invalid cXML while protecting nothing.
    /// </summary>
    [Fact]
    public void CxmlDtdSystemId_IsNotTreatedAsANetworkTarget()
    {
        Validate(DeliveryProtocolConstants.Http,
                """{"url":"https://supplier.example/orders","dtdSystemId":"http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd"}""")
            .Should().NotThrow();
    }

    // ── Configs saved BEFORE enforcement existed (the delivery-time warning) ──

    /// <summary>
    /// Refusing at save protects nothing already stored. A config saved with an http:// endpoint
    /// before enforcement keeps delivering — silently breaking it would trade a security weakness
    /// for an outage — but the dispatchers log it on every send. 2026-11-13 (plan 13b, task 8a):
    /// re-anchored from the deleted read-path composer <c>DescribeConfigWarnings</c> onto its
    /// transport half, <see cref="DeliveryConfigTransport.DescribeInsecureTransport"/>, which the
    /// HTTP, Directo and Erply delivery paths call.
    /// </summary>
    [Theory]
    [InlineData("{\"url\":\"http://erp.supplier.com/xmlcore\"}")]
    [InlineData("{\"URL\":\"http://erp.supplier.com/xmlcore\"}")]
    public void AConfigSavedBeforeEnforcement_IsFlaggedInsecure(string configJson)
    {
        var warning = DeliveryConfigTransport.DescribeInsecureTransport(DeliveryProtocolConstants.ErpDirecto, configJson);

        warning.Should().NotBeNullOrWhiteSpace();
        warning.Should().Contain("https://");
    }

    [Theory]
    [InlineData("{\"url\":\"https://supplier.example/orders\"}")]
    [InlineData("{\"url\":\"http://127.0.0.1:9000/orders\"}")]
    [InlineData("{\"host\":\"files.supplier.example\"}")]
    public void ASecureOrLoopbackConfig_CarriesNoWarning(string configJson)
    {
        DeliveryConfigTransport.DescribeInsecureTransport(DeliveryProtocolConstants.Http, configJson)
            .Should().BeNull();
    }

    /// <summary>
    /// The warning is logged. A stored userinfo URL must be flagged, but the flag must not carry
    /// the password back out into the log line.
    /// </summary>
    [Fact]
    public void AStoredUserinfoUrl_IsFlaggedWithoutEchoingTheSecret()
    {
        var warning = DeliveryConfigTransport.DescribeInsecureTransport(
            DeliveryProtocolConstants.Http, "{\"url\":\"https://apiuser:hunter2@supplier.example/orders\"}");

        warning.Should().NotBeNullOrWhiteSpace();
        warning.Should().NotContain("hunter2");
    }

    // ── The url-bearing protocol set is declared once ────────────────────────

    /// <summary>
    /// Which protocols carry an outbound URL is declared on DeliveryProtocolConstants, not retyped
    /// here. Adding a url-bearing protocol without adding it to that list makes this fail rather
    /// than silently shipping an unvalidated channel.
    /// </summary>
    [Fact]
    public void EveryDeclaredUrlBearingProtocol_RefusesCleartext_AndTheListIsNotVacuous()
    {
        DeliveryProtocolConstants.UrlBased.Should().NotBeEmpty();
        DeliveryProtocolConstants.UrlBased.Should().BeSubsetOf(DeliveryProtocolConstants.All);

        foreach (var protocol in DeliveryProtocolConstants.UrlBased)
        {
            Validate(protocol, "{\"url\":\"http://supplier.example.com/orders\",\"database\":\"live\"}")
                .Should().Throw<OutboundUrlPolicyException>($"'{protocol}' is declared url-bearing")
                .Which.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        }
    }
}
