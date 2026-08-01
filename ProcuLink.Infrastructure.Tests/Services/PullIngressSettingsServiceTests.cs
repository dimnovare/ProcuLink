using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class PullIngressSettingsServiceTests
{
    private static ProcuLinkDbContext CreateDb() =>
        new PullIngressTestDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    [Fact]
    public async Task UpdateSftp_EncryptsPassword_AndReturnsMasked()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new PullIngressSettingsService(db, encryption);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var saved = await service.UpdateSftpAsync(orgId,
            new UpdateSftpIngressRequest(true, "sftp.example", 22, "user", "s3cret", "/in", supplierId),
            default);

        saved.Enabled.Should().BeTrue();
        saved.Host.Should().Be("sftp.example");
        saved.HasPassword.Should().BeTrue();
        saved.PasswordDisplay.Should().Be("********");

        var row = await db.SftpIngressConfigs.SingleAsync();
        row.OrgId.Should().Be(orgId);
        row.EncryptedPassword.Should().NotBe("s3cret");
        encryption.Decrypt(row.EncryptedPassword).Should().Be("s3cret");

        // Re-save with null password preserves the saved secret.
        var before = row.EncryptedPassword;
        await service.UpdateSftpAsync(orgId,
            new UpdateSftpIngressRequest(true, "sftp.example", 2222, "user", null, "/in", supplierId), default);
        var after = await db.SftpIngressConfigs.SingleAsync();
        after.EncryptedPassword.Should().Be(before);
        after.Port.Should().Be(2222);
    }

    // ── SSH host-key pin lifecycle (WP-38) ───────────────────────────────────

    private static UpdateSftpIngressRequest SftpRequest(
        string host = "sftp.example", int port = 22, string? hostKeyFingerprints = null) =>
        new(true, host, port, "user", "s3cret", "/in", null, hostKeyFingerprints);

    /// <summary>
    /// The same reason the delivery config had to learn to preserve its pin: a client that has
    /// never heard of host keys sends null, and null must not silently un-pin the poller.
    /// </summary>
    [Fact]
    public async Task UpdateSftp_NullFingerprints_KeepsWhatThePollerRecorded()
    {
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);
        await service.UpdateSftpAsync(orgId, SftpRequest(), default);

        (await db.SftpIngressConfigs.SingleAsync()).HostKeyFingerprints.Should().Be("SHA256:pinned");
    }

    [Fact]
    public async Task UpdateSftp_EmptyFingerprints_ClearsThePin_TheReTrustPath()
    {
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);
        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: ""), default);

        (await db.SftpIngressConfigs.SingleAsync()).HostKeyFingerprints.Should().BeNull(
            "clearing is how an operator re-trusts a legitimately rebuilt server");
    }

    /// <summary>
    /// A pin names a SERVER. Repointing the poller at a different host leaves a fingerprint that
    /// describes something it no longer talks to — and every poll would then be refused with a
    /// message about a rebuild that never happened.
    /// </summary>
    [Fact]
    public async Task UpdateSftp_ChangingTheHost_DropsThePin()
    {
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);
        await service.UpdateSftpAsync(orgId, SftpRequest(host: "sftp.somewhere-else"), default);

        (await db.SftpIngressConfigs.SingleAsync()).HostKeyFingerprints.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSftp_ChangingThePort_DropsThePin()
    {
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);
        await service.UpdateSftpAsync(orgId, SftpRequest(port: 2222), default);

        (await db.SftpIngressConfigs.SingleAsync()).HostKeyFingerprints.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSftp_EditingSomethingElse_KeepsThePin()
    {
        // The other half of the rule: only a change of SERVER drops the pin. Re-saving the same
        // host and port — the ordinary case — must leave it alone, or the feature un-pins itself
        // on every settings save.
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);
        await service.UpdateSftpAsync(orgId,
            new UpdateSftpIngressRequest(true, "sftp.example", 22, "someone-else", null, "/elsewhere", null),
            default);

        (await db.SftpIngressConfigs.SingleAsync()).HostKeyFingerprints.Should().Be("SHA256:pinned");
    }

    [Fact]
    public async Task GetSftp_ReturnsTheRecordedFingerprintInFull()
    {
        // Not masked like the password: an operator cannot judge a changed host key without seeing
        // the value it is being compared against.
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());
        var orgId = Guid.NewGuid();

        await service.UpdateSftpAsync(orgId, SftpRequest(hostKeyFingerprints: "SHA256:pinned"), default);

        (await service.GetSftpAsync(orgId, default)).HostKeyFingerprints.Should().Be("SHA256:pinned");
    }

    [Fact]
    public async Task UpdateS3_EncryptsSecretKey_AndReturnsMasked()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var service = new PullIngressSettingsService(db, encryption);
        var orgId = Guid.NewGuid();

        var saved = await service.UpdateS3Async(orgId,
            new UpdateS3IngressRequest(true, "orders-bucket", "incoming/", "eu-west-1", "AKIA123", "super-secret", Guid.NewGuid()),
            default);

        saved.BucketName.Should().Be("orders-bucket");
        saved.AccessKeyId.Should().Be("AKIA123");
        saved.HasSecretKey.Should().BeTrue();
        saved.SecretKeyDisplay.Should().Be("********");

        var row = await db.S3IngressConfigs.SingleAsync();
        row.EncryptedSecretKey.Should().NotBe("super-secret");
        encryption.Decrypt(row.EncryptedSecretKey).Should().Be("super-secret");
    }

    [Fact]
    public async Task GetSftp_WhenNoneConfigured_ReturnsDisabledEmpty()
    {
        await using var db = CreateDb();
        var service = new PullIngressSettingsService(db, CreateEncryption());

        var result = await service.GetSftpAsync(Guid.NewGuid(), default);

        result.Enabled.Should().BeFalse();
        result.HasPassword.Should().BeFalse();
        result.PasswordDisplay.Should().BeNull();
    }

    private sealed class PullIngressTestDbContext : ProcuLinkDbContext
    {
        public PullIngressTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Keep only the two ingress-config entities; ignore everything else so the
            // InMemory provider doesn't need the jsonb/relationship config from the base context.
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
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
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<SftpIngressConfig>(b => b.HasKey(x => x.Id));
            modelBuilder.Entity<S3IngressConfig>(b => b.HasKey(x => x.Id));
        }
    }
}
