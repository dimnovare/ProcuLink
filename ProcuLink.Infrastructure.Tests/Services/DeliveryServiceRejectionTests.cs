using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Supplier-rejection ACK: verifies that a 4xx response sets
/// <c>rejected_by_supplier</c> status and populates <c>RejectionReason</c>,
/// while a 5xx keeps the existing <c>delivery_failed</c> path.
/// </summary>
public class DeliveryServiceRejectionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RejectionTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = key,
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-REJ-001",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId, artifactId);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null) =>
        new(
            db,
            new FakeFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            NullLogger<DeliveryService>.Instance);

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId,
        Guid supplierId,
        DeliveryEncryptionService encryption) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchArtifactAsync_4xxResponse_SetsRejectedBySupplierAndPopulatesRejectionReason()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Supplier rejected: unknown buyer code", 422)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ResponseCode.Should().Be(422);
        attempt.RejectionReason.Should().Be("Supplier rejected: unknown buyer code");
        attempt.ErrorMessage.Should().Be("Supplier rejected: unknown buyer code");
    }

    [Fact]
    public async Task DispatchArtifactAsync_5xxResponse_SetsDeliveryFailedAndLeavesRejectionReasonNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Gateway timeout", 503)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ResponseCode.Should().Be(503);
        attempt.RejectionReason.Should().BeNull();
        attempt.ErrorMessage.Should().Be("Gateway timeout");
    }

    [Fact]
    public async Task DispatchArtifactAsync_NetworkFailureNoCode_SetsDeliveryFailedAndLeavesRejectionReasonNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        // Simulate network failure — no response code
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Connection refused", null)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RejectionReason.Should().BeNull();
    }

    // ── Helpers shared inside this fixture ────────────────────────────────────

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public string Protocol => "http";

        public FakeDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RejectionTestDbContext : ProcuLinkDbContext
    {
        public RejectionTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
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

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<OutboundArtifact>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });

            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
