using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Delivery-path hardening — stuck-'delivering' detection + re-drive. An order committed to
/// the transient <c>delivering</c> status that never reaches a terminal state (process died,
/// artifact download threw, dispatcher hung between the status write and the terminal attempt
/// persist) is RE-DRIVEN through the delivery retry queue up to a bounded cap, after which it is
/// dead-lettered. Each action writes an audit event. Guarantees a delivering→terminal transition.
/// Uses the full ProcuLinkDbContext on InMemory.
/// </summary>
public class StuckDeliveryDetectionServiceTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    // Mirrors StuckDeliveryDetectionService.MaxRequeues.
    private const int MaxRequeues = 2;

    [Fact]
    public async Task RunAsync_StuckDeliveringOrder_FirstTime_IsReDrivenNotFailed()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Still 'delivering' (the retry job owns the next terminal transition) but bumped out of
        // the stuck window and counted — NOT failed.
        order.Status.Should().Be(OrderStatusConstants.Delivering);
        order.RequeueCount.Should().Be(1);
        order.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-30));

        // The retry job was actually re-enqueued through the seam.
        enqueuer.Calls.Should().ContainSingle()
            .Which.Should().Be((orderId, order.OrgId));

        // A StuckDeliveryRequeued audit event was written (not a dead-letter).
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckDeliveryRequeued");
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_StuckDeliveringPastRequeueCap_IsDeadLettered()
    {
        await using var db = CreateDb();
        // Already re-driven up to the cap and stranded again.
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, requeueCount: MaxRequeues,
            deliveryDueAt: DateTime.UtcNow.AddMinutes(-10), slaBreached: true);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Guaranteed delivering→terminal transition.
        order.Status.Should().Be(OrderStatusConstants.DeliveryDeadLetter);
        // SLA timer cleared on the terminal transition.
        order.DeliveryDueAt.Should().BeNull();
        order.SlaBreached.Should().BeFalse();

        // No further re-drive once the cap is hit.
        enqueuer.Calls.Should().BeEmpty();

        // Dead-letter audit with a clear reason.
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckDeliveryDeadLettered");
        var root = audit.Payload!.RootElement;
        root.GetProperty("reason").GetString().Should().Be("StuckDeliveryDeadLettered");
        root.GetProperty("deadLettered").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_RepeatedStalls_ReDriveUntilCapThenDeadLetter()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45);
        var enqueuer = new RecordingRetryEnqueuer();
        var service = CreateService(db, enqueuer);

        // Each iteration simulates: sweep re-drives -> delivery stalls again past threshold.
        for (var attempt = 1; attempt <= MaxRequeues; attempt++)
        {
            (await service.RunAsync(Threshold, default)).Should().Be(1);

            var o = await db.PurchaseOrders.SingleAsync(x => x.Id == orderId);
            o.Status.Should().Be(OrderStatusConstants.Delivering, "re-drive {0} should not fail the order", attempt);
            o.RequeueCount.Should().Be(attempt);

            // Re-stall: age the 'delivering' row back past the threshold.
            o.UpdatedAt = DateTime.UtcNow.AddMinutes(-45);
            await db.SaveChangesAsync();
        }

        // Cap now exhausted -> next sweep dead-letters.
        (await service.RunAsync(Threshold, default)).Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(x => x.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter);

        enqueuer.Calls.Should().HaveCount(MaxRequeues);
        (await db.AuditEvents.CountAsync(e => e.Action == "StuckDeliveryRequeued")).Should().Be(MaxRequeues);
        (await db.AuditEvents.CountAsync(e => e.Action == "StuckDeliveryDeadLettered")).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_RecentDeliveringOrder_IsUntouched()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 5);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Theory]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.Transforming)]
    public async Task RunAsync_OldOrderInOtherStatus_IsUntouched(string status)
    {
        // Only 'delivering' is this sweep's concern: a delivered / failed / dead-lettered order,
        // and the parsing/transforming states owned by StuckOrderDetectionService, must NOT be
        // touched — that prevents double-ownership of the same order across two sweeps.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, status, updatedMinutesAgo: 120);

        var acted = await CreateService(db, new RecordingRetryEnqueuer()).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status.Should().Be(status);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunOnReDrivenOrderDoesNothing()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45);
        var service = CreateService(db, new RecordingRetryEnqueuer());

        // First run re-drives (UpdatedAt bumped to now, so it leaves the stuck window).
        (await service.RunAsync(Threshold, default)).Should().Be(1);
        // Second run immediately after: the order is fresh again, so it is not re-acted on —
        // no double-processing before the retry job moves it to a terminal state.
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).RequeueCount.Should().Be(1);
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoEnqueuerRegistered_StuckOrderStillBumpedAndCountedNotPinned()
    {
        // A process without the retry enqueuer must still bump the order out of the stuck window
        // (so it is not re-acted on every sweep) and count the requeue — never silently pinning
        // it in 'delivering' nor permanently failing it on the first blip.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45);

        var acted = await new StuckDeliveryDetectionService(
            db, NullLogger<StuckDeliveryDetectionService>.Instance, retryEnqueuer: null)
            .RunAsync(Threshold, default);

        acted.Should().Be(1);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Delivering);
        order.RequeueCount.Should().Be(1);
        order.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-30));
        (await db.AuditEvents.SingleAsync(e => e.EntityId == orderId)).Action
            .Should().Be("StuckDeliveryRequeued");
    }

    [Fact]
    public async Task RunAsync_OnlyActsWithinThreshold_MixedAgesAndStatuses()
    {
        await using var db = CreateDb();
        var stuckOld = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 90);
        var freshDelivering = await SeedOrderAsync(db, OrderStatusConstants.Delivering, updatedMinutesAgo: 2);
        var oldDelivered = await SeedOrderAsync(db, OrderStatusConstants.Delivered, updatedMinutesAgo: 90);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);
        enqueuer.Calls.Should().ContainSingle().Which.OrderId.Should().Be(stuckOld);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == freshDelivering)).RequeueCount.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == oldDelivered)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StuckDeliveryDetectionService CreateService(
        ProcuLinkDbContext db, IRetryDeliveryEnqueuer? enqueuer = null) =>
        new(db, NullLogger<StuckDeliveryDetectionService>.Instance, enqueuer);

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db,
        string status,
        int updatedMinutesAgo,
        int requeueCount = 0,
        DateTime? deliveryDueAt = null,
        bool slaBreached = false)
    {
        var orderId = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-updatedMinutesAgo);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-STUCK-DLV",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            RequeueCount = requeueCount,
            DeliveryDueAt = deliveryDueAt,
            SlaBreached = slaBreached,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        await db.SaveChangesAsync();
        return orderId;
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
}
