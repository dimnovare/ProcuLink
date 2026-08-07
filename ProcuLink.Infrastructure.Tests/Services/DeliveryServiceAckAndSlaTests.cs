using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Group O reliability — ACK round-trip, rejection-body capture, and SLA-window bookkeeping
/// inside <see cref="DeliveryService.DispatchArtifactAsync"/>. Uses the full
/// <see cref="ProcuLinkDbContext"/> on the InMemory provider so order + attempt state is exercised.
/// </summary>
public class DeliveryServiceAckAndSlaTests
{
    [Fact]
    public async Task DispatchArtifactAsync_Success_StampsAcknowledgedAtAndClearsSlaWindow()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 202)), encryption);

        var before = DateTime.UtcNow;
        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);
        var after = DateTime.UtcNow;

        result.Success.Should().BeTrue();

        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == ids.OrderId);
        attempt.Status.Should().Be("success");
        attempt.AcknowledgedAt.Should().NotBeNull();
        attempt.AcknowledgedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

        var order = await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId);
        order.Status.Should().Be(OrderStatusConstants.Delivered);
        // A confirmed delivery closes the SLA window.
        order.DeliveryDueAt.Should().BeNull();
        order.SlaBreached.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchArtifactAsync_Failure_LeavesAcknowledgedAtNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db, new FakeDispatcher(new DeliveryResult(false, "Gateway timeout", 503)), encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == ids.OrderId);
        attempt.AcknowledgedAt.Should().BeNull();
    }

    [Fact]
    public async Task DispatchArtifactAsync_RejectionWithBody_PersistsFullResponseBody()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        const string nack = "{\"error\":\"unknown_buyer_code\",\"detail\":\"buyer ACME-42 is not registered\"}";
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "HTTP 422: supplier rejected", 422, ResponseBody: nack)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == ids.OrderId);
        // Rejection capture: full NACK body verbatim, distinct from the short ErrorMessage summary.
        attempt.ResponseBody.Should().Be(nack);
        attempt.RejectionReason.Should().Be("HTTP 422: supplier rejected");
        attempt.ResponseCode.Should().Be(422);
    }

    [Fact]
    public async Task DispatchArtifactAsync_HugeResponseBody_IsBoundedToMaxLength()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var huge = new string('x', DeliveryAttempt.MaxResponseBodyLength + 5_000);
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "HTTP 500", 500, ResponseBody: huge)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == ids.OrderId);
        attempt.ResponseBody.Should().NotBeNull();
        attempt.ResponseBody!.Length.Should().Be(DeliveryAttempt.MaxResponseBodyLength);
    }

    [Fact]
    public async Task DispatchArtifactAsync_OpensSlaWindowUsingConfiguredWindow()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        // 90-minute SLA window; a transient failure leaves the window OPEN for the sweep.
        var options = new DeliveryReliabilityOptions { SlaWindowMinutes = 90 };
        var service = CreateService(
            db, new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503)), encryption, options);

        var before = DateTime.UtcNow;
        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);
        order.DeliveryDueAt.Should().NotBeNull();
        // Deadline is ~90 minutes out from the dispatch start.
        order.DeliveryDueAt!.Value.Should().BeCloseTo(before.AddMinutes(90), TimeSpan.FromMinutes(2));
        order.SlaBreached.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchArtifactAsync_FreshAttempt_ResetsPriorSlaBreachFlag()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        // Order previously breached SLA; a new dispatch must reopen a clean window.
        var seeded = await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId);
        seeded.SlaBreached = true;
        seeded.Status = OrderStatusConstants.DeliveryFailed;
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, default);

        var order = await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId);
        order.SlaBreached.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    [Fact]
    public async Task DispatchArtifactAsync_ManualConfig_RequireAutoDeliver_NoOps_WritesNoAttempt()
    {
        // The dispatch-side gate the INLINE B1 recovery (TransformOrderJob.TryRecoverStrandedDeliveryAsync)
        // relies on: it re-enqueues delivery with requireAutoDeliver=true, so a MANUAL order
        // (AutoDeliver=false) must NO-OP at dispatch — no send, no delivery attempt row, status
        // untouched — never a force-send. Previously covered only indirectly (via the revision path).
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        var manual = MakeConfig(ids.OrgId, ids.SupplierId, encryption);
        manual.AutoDeliver = false;
        db.SupplierDeliveryConfigs.Add(manual);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeTrue(); // benign no-op success, exactly like a live AutoDeliver=false
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId)).Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.ReadyToDeliver);
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
            PoNumber = "PO-ACK-001",
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
        DeliveryEncryptionService encryption,
        DeliveryReliabilityOptions? options = null) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            options);

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId, Guid supplierId, DeliveryEncryptionService encryption) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"type\":\"none\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

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
            CancellationToken ct, string? idempotencyKey = null) => Task.FromResult(_result);
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
}
