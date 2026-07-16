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
/// A3 follow-up — the unknown-outcome park. The A3 idempotency key de-duplicates a
/// crash-recovery re-send for SFTP/FTPS (deterministic overwrite) and HTTP (Idempotency-Key,
/// if honoured), but NOT for ERP (no dedupe signal reaches the endpoint) or email
/// (caller-supplied Message-ID dedup is best-effort). On those channels a re-drive of a send
/// whose outcome we never learned is parked for a human instead of blindly repeated.
/// </summary>
public class DeliveryServiceUnconfirmedParkTests
{
    private const int MaxAttempts = 3;

    // The whole point: an Unsafe channel whose in-flight row is re-adopted must NOT be re-sent.
    [Fact]
    public async Task ReAdopt_OnUnsafeChannel_DoesNotReSend_AndParksOrderUnconfirmed()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_erply"));

        // The exact post-crash state: order still 'delivering', an in-flight 'dispatching' row
        // committed before the (unobserved) send, never finalised.
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = ids.OrderId,
            OrgId = ids.OrgId,
            Channel = "erp_erply",
            Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(0, "an unknown outcome on a channel that cannot de-duplicate must never be blindly re-sent");
        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryUnconfirmed);

        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().ContainSingle("the in-flight row is finalised in place, never duplicated");
        attempts[0].Status.Should().Be(DeliveryAttempt.StatusUnconfirmed);
        attempts[0].IdempotencyKey.Should().Be(key);
    }

    // Regression guard: today's behaviour on channels that CAN de-duplicate must not change.
    [Theory]
    [InlineData(ResendSafety.Safe)]
    [InlineData(ResendSafety.BestEffort)]
    public async Task ReAdopt_OnSafeOrBestEffortChannel_StillReSends(ResendSafety tier)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "http"));

        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = ids.OrderId,
            OrgId = ids.OrgId,
            Channel = "http",
            Destination = "https://supplier.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202), "http", tier);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(1, "an idempotent-or-best-effort channel re-drives exactly as before");
        result.Success.Should().BeTrue();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    // The common path must not park: only a RE-ADOPTED row means "we already sent this".
    [Fact]
    public async Task FirstSend_OnUnsafeChannel_DeliversNormally()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "email"));
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "email", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(1, "a first send on an unsafe channel is a normal delivery, not a park");
        result.Success.Should().BeTrue();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    // A parked order is not billable: the meter counts only delivered + rejected_by_supplier.
    [Fact]
    public async Task ParkedOrder_IsNotBillable()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_directo"));
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "erp_directo", Destination = "https://directo.example",
            Status = DeliveryAttempt.StatusDispatching, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_directo", ResendSafety.Unsafe), encryption);
        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        // Mirrors StripeBillingService.ApplyMeterStatusFilter's billable set exactly.
        var billable = await db.PurchaseOrders.CountAsync(o =>
            o.OrgId == ids.OrgId &&
            (o.Status == OrderStatusConstants.Delivered || o.Status == OrderStatusConstants.RejectedBySupplier));

        billable.Should().Be(0, "we never charge for a delivery we cannot confirm");
    }

    // ── Helpers (copied from DeliveryServiceIdempotencyTests.cs — direct sibling) ───────────

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

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status)
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
            PoNumber = "PO-PARK-1",
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

    private static DeliveryService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    // Parameterised over DeliveryServiceIdempotencyTests's version so a test can pass a
    // non-"http" protocol (erp_erply / erp_directo / email) to exercise the Unsafe park.
    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId, Guid supplierId, DeliveryEncryptionService encryption, string protocol = "http") =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = protocol,
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public string Protocol { get; }
        public ResendSafety ResendSafety { get; }

        public CountingDispatcher(DeliveryResult result, string protocol, ResendSafety resendSafety)
        {
            _result = result;
            Protocol = protocol;
            ResendSafety = resendSafety;
        }

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
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
