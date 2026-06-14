using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryServiceTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryServiceTestDbContext(options);
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

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db,
        string status = OrderStatusConstants.ReadyToDeliver)
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
            PoNumber = "PO-123",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
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

    [Fact]
    public async Task DispatchArtifactAsync_NoConfig_WritesAttemptAndMarksDeliveryFailed()
    {
        await using var db = CreateDb();
        var ids = await SeedOrderAsync(db);
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)));

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("delivery config");
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.DeliveryFailed);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.Channel.Should().Be("missing_config");
        attempt.ErrorMessage.Should().Contain("delivery config");
    }

    [Fact]
    public async Task DispatchArtifactAsync_AutoDeliverFalse_NoOpsWhenRequired()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: false));
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
    }

    [Fact]
    public async Task DispatchArtifactAsync_Success_WritesAttemptAndMarksDelivered()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 202)), encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.Delivered);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.OrderId.Should().Be(ids.OrderId);
        attempt.Status.Should().Be("success");
        attempt.ResponseCode.Should().Be(202);
    }

    [Fact]
    public async Task DispatchArtifactAsync_Failure_WritesAttemptAndMarksDeliveryFailed()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        // Use a 5xx (transient) code so the status resolves to delivery_failed, not rejected_by_supplier.
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503)), encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeFalse();
        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ErrorMessage.Should().Be("HTTP 503");
    }

    [Fact]
    public async Task DispatchArtifactAsync_StorageDownloadThrows_BecomesFailedResultNotThrow()
    {
        // A thrown storage error (e.g. an R2 clock-skew signing failure) must surface as a
        // FAILED DeliveryResult with a persisted attempt — never as an unhandled exception.
        // DeliverOrderJob runs with AutomaticRetry(Attempts = 0), so a throw here would
        // strand the order in 'delivering' with no attempt row and no retry scheduling.
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, new ThrowingFileStorage());

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("download failed");
        dispatcher.Calls.Should().Be(0, "nothing was downloaded, so nothing may be dispatched");
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.DeliveryFailed);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ErrorMessage.Should().Contain("download failed");
    }

    [Fact]
    public async Task TestFireAsync_WritesAttemptWithNullOrderId()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.SupplierDeliveryConfigs.Add(MakeConfig(orgId, supplierId, encryption, autoDeliver: false));
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        var result = await service.TestFireAsync(orgId, supplierId, default);

        result.Success.Should().BeTrue();
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.OrderId.Should().BeNull();
        attempt.OrgId.Should().Be(orgId);
        attempt.Status.Should().Be("success");
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null,
        IFileStorageService? storage = null) =>
        new(
            db,
            storage ?? new FakeFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Infrastructure.Tests.TestDoubles.FakeAnalyticsService(),
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class NoOpIntegrationTriggerService : ProcuLink.Core.Services.IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId,
        Guid supplierId,
        DeliveryEncryptionService encryption,
        bool autoDeliver) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = autoDeliver,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class FakeDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public string Protocol => "http";

        public FakeDispatcher(DeliveryResult result)
        {
            _result = result;
        }

        public Task<DeliveryResult> DispatchAsync(
            byte[] content,
            string fileName,
            string contentType,
            SupplierDeliveryConfig config,
            string decryptedCredentials,
            CancellationToken ct)
        {
            Calls++;
            content.Should().NotBeEmpty();
            new[] { "PO-123.csv", "proculink-test.csv" }.Should().Contain(fileName);
            decryptedCredentials.Should().Contain("type");
            return Task.FromResult(_result);
        }
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

    /// <summary>Simulates a transient storage failure (e.g. R2 SignatureDoesNotMatch).</summary>
    private sealed class ThrowingFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            throw new InvalidOperationException("SignatureDoesNotMatch");

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            throw new InvalidOperationException("SignatureDoesNotMatch");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("SignatureDoesNotMatch");

        public Task DeleteAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("SignatureDoesNotMatch");
    }

    private sealed class DeliveryServiceTestDbContext : ProcuLinkDbContext
    {
        public DeliveryServiceTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
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
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
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
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();

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

            // OrderExceptionService.ReconcileAsync (now invoked from DeliveryService)
            // queries lines + exceptions, so both must be mapped in this trimmed model.
            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
            });

            modelBuilder.Entity<OrderException>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
