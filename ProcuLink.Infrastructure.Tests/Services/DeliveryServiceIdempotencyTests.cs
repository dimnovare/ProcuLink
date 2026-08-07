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
/// A3 (P2) — delivery idempotency. A crash AFTER the supplier accepts a PO but BEFORE the attempt
/// row commits used to leave the order stuck in <c>delivering</c> with no attempt record; the stuck
/// re-drive then re-dispatched the IDENTICAL PO — a double delivery.
///
/// The fix commits an in-flight <c>dispatching</c> attempt row (carrying a deterministic per-artifact
/// idempotency key) BEFORE the send, and threads that key to the dispatcher. On recovery the stuck
/// re-drive re-ADOPTS the same <c>dispatching</c> row and re-sends with the SAME key (a supporting
/// supplier de-duplicates), so it can never create a second terminal attempt or a second delivery.
/// </summary>
public class DeliveryServiceIdempotencyTests
{
    private const int MaxAttempts = 3;

    [Fact]
    public async Task NormalDelivery_SendsExactlyOnce_WithDeterministicKey_AndOneTerminalAttempt()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1, "a normal delivery sends exactly once");

        var expectedKey = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        dispatcher.LastIdempotencyKey.Should().Be(expectedKey,
            "the outbound send carries the deterministic per-artifact idempotency key");

        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().ContainSingle("the in-flight row is finalised in place, not duplicated");
        attempts[0].Status.Should().Be(DeliveryAttempt.StatusSuccess);
        attempts[0].AttemptNumber.Should().Be(1);
        attempts[0].IdempotencyKey.Should().Be(expectedKey);

        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task IdempotencyKey_IsStable_AcrossReTransformOfSameOrder_ButDiffersPerArtifact()
    {
        var orderId = Guid.NewGuid();
        var artifactA = Guid.NewGuid();
        var artifactB = Guid.NewGuid();

        DeliveryService.BuildIdempotencyKey(orderId, artifactA)
            .Should().Be(DeliveryService.BuildIdempotencyKey(orderId, artifactA), "deterministic");
        DeliveryService.BuildIdempotencyKey(orderId, artifactA)
            .Should().NotBe(DeliveryService.BuildIdempotencyKey(orderId, artifactB),
                "a re-transform mints a new artifact → a legitimately new delivery");
    }

    [Fact]
    public async Task CrashAfterSendBeforeCommit_ReDrive_ReAdoptsInFlightRow_SameKey_NoSecondDelivery()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();

        // Reproduce the EXACT post-crash state: the order is still 'delivering' and a committed
        // 'dispatching' attempt row (opened just before the supplier ACK) was never finalised.
        // A crashed worker's orphan is STALE by definition — StuckDeliveryDetectionService only
        // re-drives rows stuck for many minutes, and the claim's reclaim window is 2. Seeding
        // UtcNow described a state production never re-drives (the relational claim rejects a
        // fresh 'delivering'); it passed only while the InMemory retry branch flipped
        // unconditionally. Matches the Postgres twin in DeliveryCrashRecoveryPostgresTests,
        // which ages the identical scenario by 30 minutes.
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering,
            updatedAt: DateTime.UtcNow.AddMinutes(-30));
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
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
            AttemptedAt = DateTime.UtcNow.AddMinutes(-10),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeTrue();

        // The re-send carries the SAME idempotency key the crashed attempt used → a supporting
        // supplier de-duplicates it (no second business delivery).
        dispatcher.LastIdempotencyKey.Should().Be(key);

        // The crux: the in-flight row was RE-ADOPTED, not duplicated — exactly one terminal attempt,
        // so a stuck re-drive can never manufacture a second 'delivered' for the same PO.
        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().ContainSingle("the dispatching row is re-adopted, never a second attempt");
        attempts[0].Status.Should().Be(DeliveryAttempt.StatusSuccess);
        attempts[0].AttemptNumber.Should().Be(1);

        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
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

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status, DateTime? updatedAt = null)
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
            PoNumber = "PO-IDEMP-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            CreatedAt = now,
            UpdatedAt = updatedAt ?? now,
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

    private static SupplierDeliveryConfig MakeConfig(Guid orgId, Guid supplierId, DeliveryEncryptionService encryption) =>
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

    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public string Protocol => "http";
        // Models an HTTP supplier — HTTP re-drives carry the idempotency key as a header a
        // supporting endpoint can de-duplicate, so it is not Unsafe (the default this test double
        // would otherwise inherit, which would park a re-adopted row instead of re-sending it).
        public ResendSafety ResendSafety => ResendSafety.BestEffort;

        public CapturingDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Calls++;
            LastIdempotencyKey = idempotencyKey;
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
