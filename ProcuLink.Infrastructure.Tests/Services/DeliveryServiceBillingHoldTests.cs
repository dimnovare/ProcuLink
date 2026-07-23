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
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Audit FINDING 5: a mid-pipeline billing flip must NOT silently drop a transform-ready order.
/// It is moved to the explicit, visible <c>delivery_held</c> status (auditable) and, when the org
/// returns to good standing, released back to <c>ready_to_deliver</c> and re-driven — never
/// stranded in ready_to_deliver forever. Uses the full ProcuLinkDbContext on InMemory so audit
/// events persist (unlike the ignore-heavy DeliveryServiceTests context).
/// </summary>
public class DeliveryServiceBillingHoldTests
{
    [Fact]
    public async Task HoldForBillingAsync_ReadyToDeliverOrder_MovesToDeliveryHeld_WithAudit()
    {
        await using var db = CreateDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);

        var held = await CreateService(db).HoldForBillingAsync(orgId, orderId, default);

        held.Should().BeTrue();
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Explicit held status — NOT silently left in ready_to_deliver.
        order.Status.Should().Be(OrderStatusConstants.DeliveryHeld);
        order.DeliveryDueAt.Should().BeNull("the SLA window is paused while billing-held");
        // The LIVE ROW remembers where the hold came from — the release branches on it (a held
        // park is restored, not re-driven). The audit payload alone is not queryable state.
        order.HeldFromStatus.Should().Be(OrderStatusConstants.ReadyToDeliver);

        // Visible: an audit event records the hold.
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("DeliveryHeldForBilling");
        audit.Payload!.RootElement.GetProperty("toStatus").GetString()
            .Should().Be(OrderStatusConstants.DeliveryHeld);
    }

    [Fact]
    public async Task HoldForBillingAsync_NonReadyToDeliverOrder_IsNoOp()
    {
        await using var db = CreateDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.Delivering);

        var held = await CreateService(db).HoldForBillingAsync(orgId, orderId, default);

        held.Should().BeFalse();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering);
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task ReleaseBillingHeldOrdersAsync_HeldOrders_ResetToReadyToDeliverAndReDriven()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var held1 = (await SeedOrderAsync(db, OrderStatusConstants.DeliveryHeld, orgId)).OrderId;
        var held2 = (await SeedOrderAsync(db, OrderStatusConstants.DeliveryHeld, orgId)).OrderId;
        // An already-delivering order for the SAME org must NOT be touched.
        var other = (await SeedOrderAsync(db, OrderStatusConstants.Delivering, orgId)).OrderId;
        var enqueuer = new RecordingRetryEnqueuer();

        var released = await CreateService(db, enqueuer).ReleaseBillingHeldOrdersAsync(orgId, default);

        released.Should().Be(2);
        // Re-drivable: both held orders are back in a status delivery actually picks up.
        (await db.PurchaseOrders.SingleAsync(o => o.Id == held1)).Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == held2)).Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
        // And each was actually re-enqueued for delivery.
        enqueuer.Calls.Should().BeEquivalentTo(new[] { (held1, orgId), (held2, orgId) });
        // The unrelated order is untouched.
        (await db.PurchaseOrders.SingleAsync(o => o.Id == other)).Status.Should().Be(OrderStatusConstants.Delivering);
        // A release audit event per order.
        (await db.AuditEvents.CountAsync(e => e.Action == "DeliveryHoldReleased")).Should().Be(2);
    }

    [Fact]
    public async Task ReleaseBillingHeldOrdersAsync_MixedBatch_RestoresParkWithoutReDrive_ReDrivesTheRest()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        // A held PARK: the operator's "Send again" hit the billing gate while the org was lapsed.
        var park = (await SeedOrderAsync(db, OrderStatusConstants.DeliveryHeld, orgId,
            heldFromStatus: OrderStatusConstants.DeliveryUnconfirmed)).OrderId;
        // A genuine send-ready hold (first delivery blocked by billing).
        var genuine = (await SeedOrderAsync(db, OrderStatusConstants.DeliveryHeld, orgId,
            heldFromStatus: OrderStatusConstants.ReadyToDeliver)).OrderId;
        // A legacy hold (held before HeldFromStatus existed) — must keep the old behavior.
        var legacy = (await SeedOrderAsync(db, OrderStatusConstants.DeliveryHeld, orgId)).OrderId;
        var enqueuer = new RecordingRetryEnqueuer();

        var released = await CreateService(db, enqueuer).ReleaseBillingHeldOrdersAsync(orgId, default);

        released.Should().Be(3, "every held order left the hold");

        // The park is RESTORED for its human, never auto re-sent: re-sending an unknown-outcome
        // PO on a channel that cannot de-duplicate is the duplicate the park exists to prevent.
        var restoredPark = await db.PurchaseOrders.SingleAsync(o => o.Id == park);
        restoredPark.Status.Should().Be(OrderStatusConstants.DeliveryUnconfirmed);
        restoredPark.HeldFromStatus.Should().BeNull("the hold is over; the marker must not go stale");
        // ParkUnconfirmedAsync's contract: a park keeps nagging until a human acts. The hold paused
        // the window; the restore reopens it fresh (renewed timer, mirroring the park's own reset).
        restoredPark.DeliveryDueAt.Should().NotBeNull("a restored park must resume nagging");
        restoredPark.SlaBreached.Should().BeFalse();

        // The genuine + legacy holds keep the existing release behavior: ready_to_deliver + re-drive.
        foreach (var id in new[] { genuine, legacy })
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == id);
            order.Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
            order.HeldFromStatus.Should().BeNull();
        }
        enqueuer.Calls.Should().BeEquivalentTo(new[] { (genuine, orgId), (legacy, orgId) },
            "only send-ready releases re-drive — the restored park waits for its human");

        // The audit trail tells the truth per order: the park's release names the park.
        var parkAudit = await db.AuditEvents.SingleAsync(
            e => e.EntityId == park && e.Action == "DeliveryHoldReleased");
        parkAudit.Payload!.RootElement.GetProperty("toStatus").GetString()
            .Should().Be(OrderStatusConstants.DeliveryUnconfirmed);
    }

    [Fact]
    public async Task ReleaseBillingHeldOrdersAsync_NoHeldOrders_ReturnsZero()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver, orgId);
        var enqueuer = new RecordingRetryEnqueuer();

        (await CreateService(db, enqueuer).ReleaseBillingHeldOrdersAsync(orgId, default)).Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryService CreateService(ProcuLinkDbContext db, IRetryDeliveryEnqueuer? enqueuer = null) =>
        new(
            db,
            new NoOpFileStorage(),
            CreateEncryption(),
            Array.Empty<IDeliveryDispatcher>(),
            new NoOpIntegrationTrigger(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            reliability: null,
            effectiveConfig: null,
            retryEnqueuer: enqueuer);

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

    private static async Task<(Guid OrgId, Guid OrderId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status, Guid? orgId = null, string? heldFromStatus = null)
    {
        var org = orgId ?? Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = org,
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-HOLD",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            HeldFromStatus = heldFromStatus,
            DeliveryDueAt = status == OrderStatusConstants.ReadyToDeliver ? now.AddMinutes(30) : null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (org, orderId);
    }

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpIntegrationTrigger : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NoOpFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) => Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) => Task.FromResult($"https://f/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
