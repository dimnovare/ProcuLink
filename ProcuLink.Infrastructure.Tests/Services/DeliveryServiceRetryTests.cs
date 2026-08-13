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
/// P0 reliability — operator retry/replay + dead-letter escalation.
/// Uses the full ProcuLinkDbContext on the InMemory provider so AuditEvent
/// (ignored by the stripped delivery test contexts) is exercised.
/// </summary>
public class DeliveryServiceRetryTests
{
    private const int MaxAttempts = 3;

    [Fact]
    public async Task RetryDeliveryAsync_SuccessfulDispatch_MarksDeliveredAndWritesAttempt()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 202));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == ids.OrderId);
        attempt.AttemptNumber.Should().Be(1);
        attempt.Status.Should().Be("success");
    }

    [Fact]
    public async Task RetryDeliveryAsync_IncrementsAttemptNumberOnTopOfPriorAttempts()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 1);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        var newest = await db.DeliveryAttempts
            .Where(a => a.OrderId == ids.OrderId)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstAsync();
        newest.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task RetryDeliveryAsync_FailureReachingMaxAttempts_MovesToDeadLetterWithAuditEvent()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        // 2 prior attempts already; this failing retry is the 3rd (== MaxAttempts).
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 2);
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        dispatcher.Calls.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter);
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId)).Should().Be(3);

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == ids.OrderId);
        audit.Action.Should().Be("DeliveryDeadLettered");
        audit.OrgId.Should().Be(ids.OrgId);
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RetryDeliveryAsync_AlreadyAtMaxAttempts_DeadLettersWithoutDispatching()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 3); // already at cap
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        dispatcher.Calls.Should().Be(0); // never dispatched
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter);
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId && e.Action == "DeliveryDeadLettered"))
            .Should().Be(1);
    }

    [Fact]
    public async Task RetryDeliveryAsync_AlreadyDeadLettered_IsIdempotentNoOp()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryDeadLetter);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        dispatcher.Calls.Should().Be(0);
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId)).Should().Be(0);
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId)).Should().Be(0);
    }

    [Fact]
    public async Task RetryDeliveryAsync_FailureBelowMax_StaysDeliveryFailedNoDeadLetter()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(new DeliveryResult(false, "HTTP 500", 500));
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryFailed);
        (await db.AuditEvents.CountAsync(e => e.EntityId == ids.OrderId)).Should().Be(0);
    }

    [Fact]
    public async Task RetryDeliveryAsync_WrongOrg_ReturnsNotFoundAndTouchesNothing()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        var result = await service.RetryDeliveryAsync(Guid.NewGuid(), ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Order not found.");
        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryFailed);
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
        ProcuLinkDbContext db,
        string status)
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

    private static async Task SeedPriorAttemptsAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                OrgId = orgId,
                Channel = "http",
                Destination = "https://supplier.example/orders",
                Status = "failed",
                AttemptNumber = i,
                AttemptedAt = DateTime.UtcNow.AddMinutes(-count + i),
                ResponseCode = 500,
                ErrorMessage = "prior failure",
            });
        }
        await db.SaveChangesAsync();
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
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
        public int Calls { get; private set; }
        public string Protocol => "http";

        public FakeDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content,
            string fileName,
            string contentType,
            SupplierDeliveryConfig config,
            string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
        {
            Calls++;
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
}
