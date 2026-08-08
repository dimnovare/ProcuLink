using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Transport security at the supplier delivery-config WRITE path — the one endpoint that chooses
/// where a purchase order and its credentials are sent.
///
/// <para>Before this, <c>UpsertAsync</c> never looked at the URL at all: <c>ValidateConfigJson</c>
/// checked only that the blob was non-empty and parseable. An operator could save
/// <c>http://erp.supplier.com/xmlcore</c> for Directo and every order would leave with
/// <c>user</c>, <c>password</c> and <c>key</c> as a cleartext form body, or save
/// <c>https://user:pass@supplier.example/orders</c> and have that password stored in
/// <c>config_json</c> as clear text, returned by GET, copied into every delivery attempt row, and
/// rendered on the order passport screen.</para>
///
/// <para>Both directions are asserted. A rule that refused every URL would satisfy a
/// refusal-only suite, so each refusal is paired with the save it must not break.</para>
/// </summary>
public class DeliveryConfigTransportSecurityTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryConfigServiceTests.DeliveryConfigTestDbContext(options);
    }

    private static DeliveryConfigService CreateService(ProcuLinkDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryConfigService(db, new DeliveryEncryptionService(config));
    }

    private static Task<DeliveryConfigResponse> SaveAsync(
        DeliveryConfigService service, Guid orgId, Guid supplierId, string protocol, string configJson) =>
        service.UpsertAsync(
            orgId, supplierId,
            new UpsertDeliveryConfigRequest(protocol, false, configJson, null),
            default);

    // ── Refusals ─────────────────────────────────────────────────────────────

    /// <summary>The verbatim U-3 case: Directo over cleartext ships the PO plus user/password/key.</summary>
    [Fact]
    public async Task UpsertAsync_DirectoOverPlainHttp_IsRefusedAndSavesNothing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            DeliveryProtocolConstants.ErpDirecto,
            "{\"url\":\"http://erp.supplier.com/xmlcore\",\"database\":\"live\"}");

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        thrown.And.Message.Should().Contain("https://");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(DeliveryProtocolConstants.Http)]
    [InlineData(DeliveryProtocolConstants.ErpErply)]
    [InlineData(DeliveryProtocolConstants.ErpDirecto)]
    public async Task UpsertAsync_AnyUrlBearingProtocolOverPlainHttp_IsRefused(string protocol)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), protocol,
            "{\"url\":\"http://supplier.example.com/orders\",\"database\":\"live\"}");

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
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
    public async Task UpsertAsync_UrlKeyInAnyCasing_IsStillInspected(string key)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            DeliveryProtocolConstants.Http,
            $"{{\"{key}\":\"http://supplier.example.com/orders\"}}");

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
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
    public async Task UpsertAsync_ARepeatedUrlKey_CannotSmuggleACleartextEndpointPast(string configJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            DeliveryProtocolConstants.Http, configJson);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The negative control for the case above: a blob whose repeated key is https BOTH times is
    /// unambiguous and must still save. Refusing every duplicate outright would pass the test above
    /// while turning a bypass into a different behaviour change.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ARepeatedUrlKeyThatIsSecureEveryTime_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var saved = await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            DeliveryProtocolConstants.Http,
            """{"url":"https://a.example/orders","url":"https://b.example/orders"}""");

        saved.Should().NotBeNull();
        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// config_json is cleartext by design (SupplierDeliveryConfig.cs documents the invariant).
    /// A userinfo URL puts a password straight into it — and into GET, every delivery-attempt
    /// destination, and the order passport screen that renders it.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_CredentialsEmbeddedInTheDeliveryUrl_AreRefused()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            DeliveryProtocolConstants.Http,
            "{\"url\":\"https://apiuser:hunter2@supplier.example/orders\"}");

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        thrown.And.Message.Should().NotContain("hunter2");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_ARefusedUrl_DoesNotOverwriteAWorkingConfig()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await SaveAsync(service, orgId, supplierId, DeliveryProtocolConstants.Http,
            "{\"url\":\"https://supplier.example/orders\"}");

        var act = () => SaveAsync(service, orgId, supplierId, DeliveryProtocolConstants.Http,
            "{\"url\":\"http://supplier.example/orders\"}");
        await act.Should().ThrowAsync<OutboundUrlPolicyException>();

        var row = await db.SupplierDeliveryConfigs.SingleAsync();
        row.ConfigJson.Should().Contain("https://supplier.example/orders");
    }

    // ── Allowances ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DeliveryProtocolConstants.Http)]
    [InlineData(DeliveryProtocolConstants.ErpErply)]
    [InlineData(DeliveryProtocolConstants.ErpDirecto)]
    public async Task UpsertAsync_HttpsEndpoint_IsSaved(string protocol)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var saved = await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), protocol,
            "{\"url\":\"https://supplier.example/orders\",\"database\":\"live\"}");

        saved.Protocol.Should().Be(protocol);
        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The live failure-state e2e spec stands up a Node listener on an ephemeral loopback port and
    /// saves <c>http://127.0.0.1:PORT/reject</c> as a delivery target, asserting the save succeeds.
    /// The exemption therefore has to match on host, not on an authority string with a fixed port.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:53412/reject")]
    [InlineData("http://localhost:5223/api/delivery")]
    [InlineData("http://[::1]:9000/inbound")]
    public async Task UpsertAsync_PlainHttpOnLoopback_IsSavedSoLocalDevelopmentKeepsWorking(string url)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), DeliveryProtocolConstants.Http,
            $"{{\"url\":\"{url}\"}}");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
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
    public async Task UpsertAsync_HostBasedProtocols_AreUnaffected(string protocol)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), protocol,
            "{\"host\":\"files.supplier.example\",\"port\":22,\"remotePath\":\"/in\"}");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// REGRESSION GUARD. <c>dtdSystemId</c> is a cXML DOCTYPE SYSTEM identifier — an XML identity
    /// string emitted into the output document and never fetched (the loaders pin
    /// <c>DtdProcessing.Ignore</c> and a null <c>XmlResolver</c>). The canonical value published by
    /// cXML is <c>http://xml.cxml.org/...</c>, and the editor ships it as a datalist option.
    /// Forcing https on it would emit invalid cXML while protecting nothing.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_CxmlDtdSystemId_IsNotTreatedAsANetworkTarget()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, new UpsertDeliveryConfigRequest(
            DeliveryProtocolConstants.Http, false,
            "{\"url\":\"https://supplier.example/orders\"}", null, "cxml",
            new CxmlCredentialsInput(
                "NetworkId", "REDACTED-NETWORK-ID", null, null, null, null,
                SenderSharedSecret: null,
                DtdSystemId: "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd",
                DtdPublicId: "-//cXML//DTD cXML 1.2.024//EN")), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);
        fetched!.CxmlCredentials!.DtdSystemId
            .Should().Be("http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd");
    }

    // ── Configs saved BEFORE enforcement existed ─────────────────────────────

    /// <summary>
    /// Refusing at save protects nothing already stored. An org that saved an http:// endpoint
    /// before this shipped keeps delivering — silently breaking those orders would trade a
    /// security weakness for an outage — but the config is flagged on every read so the editor
    /// can say so, rather than leaving the operator exposed and uninformed.
    /// </summary>
    [Theory]
    [InlineData("{\"url\":\"http://erp.supplier.com/xmlcore\"}")]
    [InlineData("{\"URL\":\"http://erp.supplier.com/xmlcore\"}")]
    public async Task GetAsync_AConfigSavedBeforeEnforcement_IsFlaggedInsecure(string configJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Written straight to the row: this is what an org that configured delivery before
        // enforcement existed actually has, and UpsertAsync can no longer produce it.
        db.SupplierDeliveryConfigs.Add(new ProcuLink.Core.Entities.SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.ErpDirecto, ConfigJson = configJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        fetched.InsecureTransportWarning.Should().Contain("https://");
    }

    [Theory]
    [InlineData("{\"url\":\"https://supplier.example/orders\"}")]
    [InlineData("{\"url\":\"http://127.0.0.1:9000/orders\"}")]
    [InlineData("{\"host\":\"files.supplier.example\"}")]
    public async Task GetAsync_ASecureOrLoopbackConfig_CarriesNoWarning(string configJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.SupplierDeliveryConfigs.Add(new ProcuLink.Core.Entities.SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Http, ConfigJson = configJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        (await service.GetAsync(orgId, supplierId, default))!
            .InsecureTransportWarning.Should().BeNull();
    }

    /// <summary>
    /// The warning is rendered in the editor and logged. A stored userinfo URL must be flagged,
    /// but the flag must not carry the password back out into either place.
    /// </summary>
    [Fact]
    public async Task GetAsync_AStoredUserinfoUrl_IsFlaggedWithoutEchoingTheSecret()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.SupplierDeliveryConfigs.Add(new ProcuLink.Core.Entities.SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Http,
            ConfigJson = "{\"url\":\"https://apiuser:hunter2@supplier.example/orders\"}",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        fetched.InsecureTransportWarning.Should().NotContain("hunter2");
    }

    // ── The url-bearing protocol set is declared once ────────────────────────

    /// <summary>
    /// Which protocols carry an outbound URL is declared on DeliveryProtocolConstants, not retyped
    /// here. Adding a url-bearing protocol without adding it to that list makes this fail rather
    /// than silently shipping an unvalidated channel.
    /// </summary>
    [Fact]
    public async Task EveryDeclaredUrlBearingProtocol_RefusesCleartext_AndTheListIsNotVacuous()
    {
        DeliveryProtocolConstants.UrlBased.Should().NotBeEmpty();
        DeliveryProtocolConstants.UrlBased.Should().BeSubsetOf(DeliveryProtocolConstants.All);

        foreach (var protocol in DeliveryProtocolConstants.UrlBased)
        {
            await using var db = CreateDb();
            var service = CreateService(db);

            var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), protocol,
                "{\"url\":\"http://supplier.example.com/orders\",\"database\":\"live\"}");

            (await act.Should().ThrowAsync<OutboundUrlPolicyException>(
                    $"'{protocol}' is declared url-bearing"))
                .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        }
    }
}
