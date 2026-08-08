using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryConfigServiceTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryConfigTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = key
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    [Fact]
    public async Task UpsertAsync_CreatesOrgScopedConfigAndEncryptsCredentials()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new DeliveryConfigService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var credentials = "{\"type\":\"apikey\",\"value\":\"secret\"}";

        var saved = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest(
                "HTTP",
                true,
                "{\"url\":\"https://supplier.example/orders\"}",
                credentials),
            default);

        saved.SupplierId.Should().Be(supplierId);
        saved.Protocol.Should().Be("http");
        saved.AutoDeliver.Should().BeTrue();
        saved.HasCredentials.Should().BeTrue();
        saved.CredentialsDisplay.Should().Be("********");

        var row = await db.SupplierDeliveryConfigs.SingleAsync();
        row.OrgId.Should().Be(orgId);
        row.SupplierId.Should().Be(supplierId);
        row.EncryptedCredentials.Should().NotBe(credentials);
        encryption.Decrypt(
            row.EncryptedCredentials,
            CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId))
            .Should().Be(credentials);
    }

    [Fact]
    public async Task GetAsync_ReturnsRedactedCredentialsOnly()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new DeliveryConfigService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var credentials = "{\"type\":\"bearer\",\"token\":\"secret-token\"}";

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://supplier.example/orders\"}", credentials),
            default);

        var result = await service.GetAsync(orgId, supplierId, default);

        result.Should().NotBeNull();
        result!.HasCredentials.Should().BeTrue();
        result.CredentialsDisplay.Should().Be("********");
        result.ToString().Should().NotContain("secret-token");
    }

    [Fact]
    public async Task UpsertAsync_NullCredentials_PreservesExistingCredentials()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new DeliveryConfigService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var credentials = "{\"type\":\"basic\",\"username\":\"u\",\"password\":\"p\"}";

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", credentials),
            default);

        var before = (await db.SupplierDeliveryConfigs.SingleAsync()).EncryptedCredentials;

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", true, "{\"url\":\"https://b.example\"}", null),
            default);

        var after = await db.SupplierDeliveryConfigs.SingleAsync();
        after.EncryptedCredentials.Should().Be(before);
        after.AutoDeliver.Should().BeTrue();
        after.ConfigJson.Should().Contain("b.example");
    }

    [Fact]
    public async Task UpsertAsync_OmittedAutoTransform_PreservesExistingAutoTransform()
    {
        // WP-33's switch used to be a non-nullable bool defaulting to false, so an unrelated save
        // — a changed URL, a new timeout — silently turned auto-send off. No frontend sends the
        // property, so EVERY save did it. Latent only until stage 2 gives someone a way to turn it
        // on, and then the symptom is "auto-send stopped working" with nothing in the audit trail
        // to explain it.
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest(
                "http", false, "{\"url\":\"https://a.example\"}", null, AutoTransform: true),
            default);

        (await db.SupplierDeliveryConfigs.SingleAsync()).AutoTransform.Should().BeTrue();

        // A save that says nothing about auto-send — the shape every caller sends today.
        var response = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://b.example\"}", null),
            default);

        var after = await db.SupplierDeliveryConfigs.SingleAsync();
        after.AutoTransform.Should().BeTrue();
        after.ConfigJson.Should().Contain("b.example");
        // The read-back must agree with the row, or the UI shows a switch in the wrong position.
        response.AutoTransform.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_ExplicitFalseAutoTransform_TurnsItOff()
    {
        // The other half: null means keep, so switching auto-send OFF has to still work. Without
        // this, "preserve on omit" is indistinguishable from "can never be turned off".
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest(
                "http", false, "{\"url\":\"https://a.example\"}", null, AutoTransform: true),
            default);

        var response = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest(
                "http", false, "{\"url\":\"https://a.example\"}", null, AutoTransform: false),
            default);

        (await db.SupplierDeliveryConfigs.SingleAsync()).AutoTransform.Should().BeFalse();
        response.AutoTransform.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_OmittedAutoTransform_OnACreate_DefaultsToOff()
    {
        // A brand-new config has nothing to keep, so "null = keep" must land on the entity default
        // rather than on whatever a stale row held. Off is the safe default for a switch that
        // sends orders without anyone asking.
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());

        var response = await service.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null),
            default);

        (await db.SupplierDeliveryConfigs.SingleAsync()).AutoTransform.Should().BeFalse();
        response.AutoTransform.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_EmptyCredentials_ClearsExistingCredentials()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", "{\"type\":\"none\"}"),
            default);

        await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", ""),
            default);

        var result = await service.GetAsync(orgId, supplierId, default);
        result!.HasCredentials.Should().BeFalse();
        result.CredentialsDisplay.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_DoesNotReturnOtherOrgConfig()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(
            Guid.NewGuid(),
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null),
            default);

        var result = await service.GetAsync(Guid.NewGuid(), supplierId, default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_IsNoOpWhenMissing()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());

        await service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_AcceptsErpProtocols()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var erply = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("ERP_ERPLY", false, "{\"url\":\"https://erply.example/api\"}", null),
            default);

        erply.Protocol.Should().Be("erp_erply");

        var directo = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("erp_directo", false, "{\"url\":\"https://login.directo.ee/xmlcore\"}", null),
            default);

        directo.Protocol.Should().Be("erp_directo");
    }

    [Fact]
    public async Task UpsertAsync_RejectsUnknownProtocol()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());

        var act = () => service.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new UpsertDeliveryConfigRequest("peppol", false, "{\"url\":\"https://a.example\"}", null),
            default);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Delivery protocol must be http, sftp, ftp, ftps, email, erp_erply, or erp_directo.*");
    }

    [Fact]
    public async Task UpsertAsync_NormalizesAndPersistsOutputFormat()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var saved = await service.UpsertAsync(
            orgId,
            supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null, "CXML"),
            default);

        saved.OutputFormat.Should().Be("cxml");

        var fetched = await service.GetAsync(orgId, supplierId, default);
        fetched!.OutputFormat.Should().Be("cxml");
    }

    [Fact]
    public async Task UpsertAsync_RejectsUnknownOutputFormat()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());

        var act = () => service.UpsertAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null, "edifact"),
            default);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Output format must be one of: xml, csv, cxml, json, ubl, x12.*");
    }

    // ── cXML network credentials ───────────────────────────────────────────────

    private static UpsertDeliveryConfigRequest CxmlReq(CxmlCredentialsInput cxml) =>
        new("http", false, "{\"url\":\"https://a.example\"}", null, "cxml", cxml);

    [Fact]
    public async Task UpsertAsync_PersistsCxmlIdentities_AndEncryptsSharedSecret()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new DeliveryConfigService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var saved = await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            FromDomain: "NetworkId", FromIdentity: "TESTBUYER_SE",
            ToDomain: "NetworkId", ToIdentity: "TESTSUPPLIER_SE",
            SenderDomain: "NetworkId", SenderIdentity: "TESTBUYER_SE",
            SenderSharedSecret: "top-secret")), default);

        saved.CxmlCredentials.Should().NotBeNull();
        saved.CxmlCredentials!.FromIdentity.Should().Be("TESTBUYER_SE");
        saved.CxmlCredentials.ToIdentity.Should().Be("TESTSUPPLIER_SE");
        saved.CxmlCredentials.SenderDomain.Should().Be("NetworkId");
        saved.CxmlCredentials.HasSharedSecret.Should().BeTrue();

        // The secret is encrypted at rest and never echoed back in the response.
        var row = await db.SupplierDeliveryConfigs.SingleAsync();
        row.EncryptedCxmlSharedSecret.Should().NotBeNullOrEmpty().And.NotBe("top-secret");
        encryption.Decrypt(
            row.EncryptedCxmlSharedSecret!,
            CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCxmlSecret, supplierId))
            .Should().Be("top-secret");
        saved.ToString().Should().NotContain("top-secret");
        row.CxmlConfigJson.Should().Contain("TESTBUYER_SE").And.NotContain("top-secret");
    }

    [Fact]
    public async Task GetAsync_ReturnsCxmlIdentitiesAndHasSecretFlag_NeverTheSecret()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE", "shh")), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.CxmlCredentials.Should().NotBeNull();
        fetched.CxmlCredentials!.FromIdentity.Should().Be("TESTBUYER_SE");
        fetched.CxmlCredentials.HasSharedSecret.Should().BeTrue();
        fetched.ToString().Should().NotContain("shh");
    }

    [Fact]
    public async Task UpsertThenGet_RoundTripsConfigurableDtd()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", null, null, null, null,
            SenderSharedSecret: null,
            DtdSystemId: "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd",
            DtdPublicId: "-//cXML//DTD cXML 1.2.024//EN")), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);

        // The editor reads the DTD back to pre-fill its inputs (preview == delivery).
        fetched!.CxmlCredentials!.DtdSystemId.Should().Be("http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd");
        fetched.CxmlCredentials.DtdPublicId.Should().Be("-//cXML//DTD cXML 1.2.024//EN");
    }

    [Fact]
    public async Task UpsertThenGet_NoDtd_LeavesDtdNull()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", null, null, null, null, "shh")), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.CxmlCredentials!.DtdSystemId.Should().BeNull();
        fetched.CxmlCredentials.DtdPublicId.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_NullSharedSecret_KeepsExistingSecret_ButUpdatesIdentities()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new DeliveryConfigService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE", "first-secret")), default);
        var before = (await db.SupplierDeliveryConfigs.SingleAsync()).EncryptedCxmlSharedSecret;

        // Re-save with identities edited but NO secret (write-only leave-blank-to-keep).
        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_DK", "NetworkId", "TESTSUPPLIER_DK", "NetworkId", "TESTBUYER_DK",
            SenderSharedSecret: null)), default);

        var after = await db.SupplierDeliveryConfigs.SingleAsync();
        after.EncryptedCxmlSharedSecret.Should().Be(before, "a null secret must keep the saved one");
        encryption.Decrypt(
            after.EncryptedCxmlSharedSecret!,
            CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCxmlSecret, supplierId))
            .Should().Be("first-secret");
        after.CxmlConfigJson.Should().Contain("TESTBUYER_DK"); // identities still updated
    }

    [Fact]
    public async Task UpsertAsync_BlankSharedSecret_ClearsSecret_ButKeepsIdentities()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE", "secret")), default);

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE",
            SenderSharedSecret: "")), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);
        fetched!.CxmlCredentials!.HasSharedSecret.Should().BeFalse();
        fetched.CxmlCredentials.FromIdentity.Should().Be("TESTBUYER_SE");
    }

    [Fact]
    public async Task UpsertAsync_NullCxmlBlock_LeavesSavedCxmlCredentialsUntouched()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId, CxmlReq(new CxmlCredentialsInput(
            "NetworkId", "TESTBUYER_SE", "NetworkId", "TESTSUPPLIER_SE", "NetworkId", "TESTBUYER_SE", "secret")), default);

        // A normal save WITHOUT a cXML block (e.g. editing the URL) must not wipe cXML credentials.
        await service.UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", true, "{\"url\":\"https://b.example\"}", null, "cxml"), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);
        fetched!.CxmlCredentials.Should().NotBeNull();
        fetched.CxmlCredentials!.FromIdentity.Should().Be("TESTBUYER_SE");
        fetched.CxmlCredentials.HasSharedSecret.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_NoCxmlConfigured_ReturnsNullCxmlBlock()
    {
        await using var db = CreateDb();
        var service = new DeliveryConfigService(db, CreateEncryption());
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await service.UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://a.example\"}", null, "cxml"), default);

        var fetched = await service.GetAsync(orgId, supplierId, default);
        fetched!.CxmlCredentials.Should().BeNull();
    }

    // internal, not private: DeliveryConfigTransportSecurityTests needs the same trimmed model,
    // and duplicating this Ignore list would drift the moment an entity is added.
    internal sealed class DeliveryConfigTestDbContext : ProcuLinkDbContext
    {
        public DeliveryConfigTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });
        }
    }
}
