using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class EmailSettingsServiceTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new EmailSettingsTestDbContext(options);
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
    public async Task GetAsync_NoConfig_ReturnsDisabledDefaults()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = "org_1",
            Name = "Nordic Distribution",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = new EmailSettingsService(db, CreateEncryption());

        var result = await service.GetAsync(orgId, default);

        result.Enabled.Should().BeFalse();
        result.Host.Should().BeEmpty();
        result.Port.Should().Be(993);
        result.UseSsl.Should().BeTrue();
        result.Folder.Should().Be("INBOX");
        result.HasPassword.Should().BeFalse();
        result.PasswordDisplay.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_SavesConfigAndEncryptsPassword()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = await SeedOrgAsync(db);
        var supplierId = await SeedSupplierAsync(db, orgId);
        var service = new EmailSettingsService(db, encryption);

        var result = await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            Enabled: true,
            Host: "imap.example.com",
            Port: 993,
            UseSsl: true,
            Username: "orders@example.com",
            Password: "secret",
            Folder: "Orders",
            DefaultSupplierId: supplierId), default);

        result.Enabled.Should().BeTrue();
        result.HasPassword.Should().BeTrue();
        result.PasswordDisplay.Should().Be("********");

        var org = await db.Organisations.SingleAsync(x => x.Id == orgId);
        org.EmailConfigJson.Should().Contain("imap.example.com");
        org.EmailConfigJson.Should().NotContain("secret");

        var stored = EmailPollingConfig.FromJson(org.EmailConfigJson);
        encryption.Decrypt(stored.PasswordCiphertext!).Should().Be("secret");
    }

    [Fact]
    public async Task UpdateAsync_NullPassword_PreservesExistingPassword()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = await SeedOrgAsync(db);
        var supplierId = await SeedSupplierAsync(db, orgId);
        var service = new EmailSettingsService(db, encryption);

        await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            true, "imap-a.example.com", 993, true, "orders@example.com", "secret", "INBOX", supplierId), default);
        var before = EmailPollingConfig.FromJson((await db.Organisations.SingleAsync()).EmailConfigJson).PasswordCiphertext;

        await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            true, "imap-b.example.com", 993, true, "orders@example.com", null, "Orders", supplierId), default);

        var after = EmailPollingConfig.FromJson((await db.Organisations.SingleAsync()).EmailConfigJson);
        after.Host.Should().Be("imap-b.example.com");
        after.PasswordCiphertext.Should().Be(before);
    }

    [Fact]
    public async Task UpdateAsync_EmptyPassword_ClearsExistingPassword()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        var supplierId = await SeedSupplierAsync(db, orgId);
        var service = new EmailSettingsService(db, CreateEncryption());

        await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            true, "imap.example.com", 993, true, "orders@example.com", "secret", "INBOX", supplierId), default);

        var result = await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            false, "imap.example.com", 993, true, "orders@example.com", "", "INBOX", supplierId), default);

        result.HasPassword.Should().BeFalse();
        var stored = EmailPollingConfig.FromJson((await db.Organisations.SingleAsync()).EmailConfigJson);
        stored.PasswordCiphertext.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_SetsEmailPollingEnabledFlag_MirroringConfigPresence()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        var supplierId = await SeedSupplierAsync(db, orgId);
        var service = new EmailSettingsService(db, CreateEncryption());

        // Before any config the org is NOT a poller candidate.
        (await db.Organisations.SingleAsync(x => x.Id == orgId)).EmailPollingEnabled.Should().BeFalse();

        await service.UpdateAsync(orgId, new UpdateEmailSettingsRequest(
            Enabled: true, Host: "imap.example.com", Port: 993, UseSsl: true,
            Username: "orders@example.com", Password: "secret", Folder: "INBOX",
            DefaultSupplierId: supplierId), default);

        // The indexed poller-candidate flag now mirrors the non-empty email config —
        // the same set the legacy "email_config <> '{}'" predicate enqueued, so the
        // EmailPollingJob behaviour is preserved while filtering on an index.
        var org = await db.Organisations.SingleAsync(x => x.Id == orgId);
        org.EmailPollingEnabled.Should().BeTrue();
        org.EmailConfigJson.Should().NotBe("{}");
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Nordic Distribution",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Acme Components",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private sealed class EmailSettingsTestDbContext : ProcuLinkDbContext
    {
        public EmailSettingsTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
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

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
            });
        }
    }
}
