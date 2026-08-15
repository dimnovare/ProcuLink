using System.Text.Json;
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
/// B-13 (P1) — reaching the retry cap notified nobody.
///
/// <para>
/// Every surface the dead-letter transition had was PULL-based: the critical <c>dead_letter</c>
/// exception row, <c>OpsController.GetDeadLetter</c>, <c>OrdersController.GetDeadLetterCount</c>.
/// All of them require somebody to already be looking, so the first notice a procurement team got
/// that a PO would never be sent was the supplier telephoning to ask where it was. These tests pin
/// that the terminal transition now pushes an <c>order.dead_lettered</c> integration event, and —
/// just as important — that it pushes exactly ONE.
/// </para>
///
/// <para>
/// <b>Anti-vacuity.</b> A test that asserts "a notification was sent" passes against a harness that
/// cannot observe anything, so <see cref="RetryDeliveryAsync_FailureBelowMax_EmitsOrderFailedButNotDeadLettered"/>
/// is the paired control: it asserts the SAME recording fake captures the pre-existing
/// <c>order.failed</c> event on the same code path. If the fake ever stopped recording — or were
/// swapped back to the no-op double this suite was cloned from — that control goes red, which is
/// what makes the positive assertions in the other tests mean something.
/// </para>
/// </summary>
public class DeliveryServiceDeadLetterNotificationTests
{
    private const int MaxAttempts = 3;

    [Fact]
    public async Task RetryDeliveryAsync_FailureReachingMaxAttempts_EmitsOrderDeadLetteredExactlyOnce()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        // 2 prior attempts; this failing retry is the 3rd (== MaxAttempts) and dead-letters.
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 2);
        await db.SaveChangesAsync();

        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503)), encryption, trigger);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter, "the retry budget is spent");

        var deadLettered = trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered);
        deadLettered.Should().HaveCount(1,
            "the customer must be told exactly once that this order is never going to be sent");
        deadLettered[0].OrgId.Should().Be(ids.OrgId, "the event must be scoped to the owning org");
    }

    [Fact]
    public async Task OrderDeadLetteredPayload_CarriesTheOrderIdAndTheTerminalCause()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 2);
        await db.SaveChangesAsync();

        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503)), encryption, trigger);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        var payload = JsonDocument
            .Parse(JsonSerializer.Serialize(trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered).Single().Payload))
            .RootElement;

        payload.GetProperty("order_id").GetGuid().Should().Be(ids.OrderId,
            "a subscriber's only way to act on this is to resolve the order it names");
        payload.GetProperty("attempt_count").GetInt32().Should().Be(MaxAttempts);
        payload.GetProperty("error").GetString().Should().Be("HTTP 503",
            "the last error is what tells the customer whether to re-send or fix the supplier config");
        payload.TryGetProperty("dead_lettered_at", out _).Should().BeTrue();
    }

    /// <summary>
    /// The anti-vacuity control described in the class summary, doing double duty as the
    /// under-the-cap negative: a failed attempt that still has budget left must NOT claim the order
    /// is dead. Emitting the terminal event early would tell a customer to re-route a PO that the
    /// very next retry delivers.
    /// </summary>
    [Fact]
    public async Task RetryDeliveryAsync_FailureBelowMax_EmitsOrderFailedButNotDeadLettered()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync(); // no prior attempts — this is attempt 1 of 3

        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "HTTP 500", 500)), encryption, trigger);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        (await db.PurchaseOrders.SingleAsync(p => p.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryFailed, "there is still retry budget left");

        // ANTI-VACUITY: this fake really does observe events. Without this assertion every
        // "was emitted" claim in this file would also pass against a recorder that captures nothing.
        trigger.EventsOf(IntegrationEventTypes.OrderFailed).Should().HaveCount(1,
            "the per-attempt failure event is pre-existing behaviour and proves the recorder is wired");

        trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered).Should().BeEmpty(
            "an order with retries remaining is not dead — saying so would be a false terminal claim");
    }

    [Fact]
    public async Task RetryDeliveryAsync_AlreadyAtMaxAttempts_DeadLettersWithoutDispatching_AndStillNotifies()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 3); // already at cap
        await db.SaveChangesAsync();

        var trigger = new RecordingIntegrationTriggerService();
        var dispatcher = new FakeDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, trigger);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(0, "the cap was already spent, so nothing is dispatched");
        // The second of the two DeadLetterAsync call sites. Covering only the dispatch-failure site
        // would leave this one silent, which is the shape the original defect had.
        trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered).Should().HaveCount(1);
    }

    /// <summary>
    /// Idempotency. Hangfire re-runs the delivery job, and an ops re-drive can call the retry path
    /// against an order that is already terminal. <c>RetryDeliveryAsync</c>'s
    /// <c>order.Status == DeliveryDeadLetter</c> guard returns NotRetryable before either
    /// <c>DeadLetterAsync</c> call site, and enqueuing the event AFTER the status commit is what
    /// makes that guard load-bearing — the barrier is durable before the notification is raised.
    /// Five "your PO is dead" webhooks for one order trains a customer to ignore the sixth.
    /// </summary>
    [Fact]
    public async Task RetryDeliveryAsync_AlreadyDeadLettered_EmitsNoFurtherNotification()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryDeadLetter);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption, trigger);

        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        trigger.Events.Should().BeEmpty(
            "a re-drive of an already dead-lettered order re-notifies nobody");
    }

    [Fact]
    public async Task RepeatedRetriesAfterDeadLetter_NotifyOnlyOnce()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await SeedPriorAttemptsAsync(db, ids.OrgId, ids.OrderId, count: 2);
        await db.SaveChangesAsync();

        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "HTTP 503", 503)), encryption, trigger);

        // Simulates a Hangfire retry storm re-driving the same order five times.
        for (var i = 0; i < 5; i++)
            await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered).Should().HaveCount(1,
            "the dead-letter status guard makes the notification at-most-once across re-drives");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class RecordingIntegrationTriggerService : IIntegrationTriggerService
    {
        public List<(Guid OrgId, string EventType, object Payload)> Events { get; } = new();

        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
        {
            Events.Add((organisationId, eventType, payload));
            return Task.CompletedTask;
        }

        public IReadOnlyList<(Guid OrgId, string EventType, object Payload)> EventsOf(string eventType) =>
            Events.Where(e => e.EventType == eventType).ToList();
    }

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

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption,
        IIntegrationTriggerService trigger) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            trigger,
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

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
