using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
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
        encryption.Decrypt(row.EncryptedCredentials).Should().Be(credentials);
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
            .WithMessage("Delivery protocol must be http, sftp, ftp, ftps, smtp, erp_erply, or erp_directo.*");
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

    private sealed class DeliveryConfigTestDbContext : ProcuLinkDbContext
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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
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
