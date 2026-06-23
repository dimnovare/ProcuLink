using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// P0 reliability — stuck-order detection + requeue. Orders left in a transient
/// pipeline status ('parsing' / 'transforming') past the timeout are RE-ENQUEUED
/// (a transient Worker restart mid-job is recoverable), up to a bounded cap, after
/// which they are dead-lettered as genuinely failed. Each action writes an audit
/// event. Uses the full ProcuLinkDbContext on InMemory.
/// </summary>
public class StuckOrderDetectionServiceTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    // Mirrors StuckOrderDetectionService.MaxRequeues.
    private const int MaxRequeues = 2;

    [Fact]
    public async Task RunAsync_StuckParsingOrder_FirstTime_IsRequeuedNotFailed()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Reset to its pre-parse state — NOT failed.
        order.Status.Should().Be(OrderStatusConstants.PendingParse);
        order.RequeueCount.Should().Be(1);

        // The parse job was actually re-enqueued through the seam.
        enqueuer.Calls.Should().ContainSingle()
            .Which.Should().Be((orderId, order.OrgId));

        // A StuckRequeued audit event was written (not StuckTimeout).
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckRequeued");
        audit.EntityType.Should().Be("Order");
    }

    [Fact]
    public async Task RunAsync_StuckTransformingOrder_FirstTime_IsResetToReadyNotFailed()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Transforming, updatedMinutesAgo: 60);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Reset to the resolved pre-transform 'ready' state — NOT failed.
        order.Status.Should().Be(OrderStatusConstants.Ready);
        order.RequeueCount.Should().Be(1);

        // Transform recovery does NOT use the parse seam.
        enqueuer.Calls.Should().BeEmpty();

        (await db.AuditEvents.SingleAsync(e => e.EntityId == orderId)).Action
            .Should().Be("StuckRequeued");
    }

    [Fact]
    public async Task RunAsync_StuckOrderPastRequeueCap_IsDeadLetteredAsFailed()
    {
        await using var db = CreateDb();
        // Already requeued up to the cap and stalled again.
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45, requeueCount: MaxRequeues);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Failed);

        // No further requeue once the cap is hit.
        enqueuer.Calls.Should().BeEmpty();

        // Dead-letter audit with a clear reason.
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckTimeout");
        var root = audit.Payload!.RootElement;
        root.GetProperty("reason").GetString().Should().Be("StuckTimeout");
        root.GetProperty("deadLettered").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_StuckTransformingOrderPastRequeueCap_RecoversToReadyNotFailed()
    {
        // A 'transforming' strand is NOT a genuine failure: a transform job that actually ran
        // and failed reverts itself to 'ready', so a strand the sweep still sees past the cap is
        // the rare "claimed but no job ran" crash window. It must recover to the healthy,
        // re-sendable 'ready' state — never terminal Failed (the delivery sweep's analogue
        // dead-letters to the RECOVERABLE delivery_dead_letter, not Failed).
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Transforming, updatedMinutesAgo: 60, requeueCount: MaxRequeues);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Recovered to a healthy, re-sendable state — NOT terminal Failed.
        order.Status.Should().Be(OrderStatusConstants.Ready);
        // Requeue budget reset so a future genuine stall gets fresh attempts.
        order.RequeueCount.Should().Be(0);

        // Recovery does not use the parse seam.
        enqueuer.Calls.Should().BeEmpty();

        // A recovery (not a dead-letter) audit event is written.
        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckTransformRecovered");
        var root = audit.Payload!.RootElement;
        root.GetProperty("reason").GetString().Should().Be("StuckTransformRecovered");
        root.GetProperty("deadLettered").GetBoolean().Should().BeFalse();
        root.GetProperty("toStatus").GetString().Should().Be(OrderStatusConstants.Ready);
        // The stalled count is reported even though it is reset on the order.
        root.GetProperty("requeueCount").GetInt32().Should().Be(MaxRequeues);
    }

    [Fact]
    public async Task RunAsync_RepeatedStalls_RequeueUntilCapThenDeadLetter()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);
        var enqueuer = new RecordingParseEnqueuer();
        var service = CreateService(db, enqueuer);

        // Each iteration simulates: sweep requeues -> job stalls again past threshold.
        for (var attempt = 1; attempt <= MaxRequeues; attempt++)
        {
            (await service.RunAsync(Threshold, default)).Should().Be(1);

            var o = await db.PurchaseOrders.SingleAsync(x => x.Id == orderId);
            o.Status.Should().Be(OrderStatusConstants.PendingParse, "requeue {0} should not fail the order", attempt);
            o.RequeueCount.Should().Be(attempt);

            // Re-stall: put it back in 'parsing', aged past the threshold.
            o.Status = OrderStatusConstants.Parsing;
            o.UpdatedAt = DateTime.UtcNow.AddMinutes(-45);
            await db.SaveChangesAsync();
        }

        // Cap now exhausted -> next sweep dead-letters.
        (await service.RunAsync(Threshold, default)).Should().Be(1);
        (await db.PurchaseOrders.SingleAsync(x => x.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Failed);

        enqueuer.Calls.Should().HaveCount(MaxRequeues);
        (await db.AuditEvents.CountAsync(e => e.Action == "StuckRequeued")).Should().Be(MaxRequeues);
        (await db.AuditEvents.CountAsync(e => e.Action == "StuckTimeout")).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_RecentTransientOrder_IsUntouched()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 5);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Parsing);
        enqueuer.Calls.Should().BeEmpty();
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_OldOrderInNonTransientStatus_IsUntouched()
    {
        await using var db = CreateDb();
        // A delivered order updated long ago must NOT be touched — only
        // parsing/transforming count as "stuck".
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Delivered, updatedMinutesAgo: 120);

        var acted = await CreateService(db).RunAsync(Threshold, default);

        acted.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_SecondRunOnRequeuedOrderDoesNothing()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);
        var service = CreateService(db, new RecordingParseEnqueuer());

        // First run requeues (status leaves the transient set).
        (await service.RunAsync(Threshold, default)).Should().Be(1);
        // Second run: the order is now 'pending_parse', not in a transient status,
        // so it is not acted on again — no double-processing.
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).RequeueCount.Should().Be(1);
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoEnqueuerRegistered_ParsingOrderStillResetAndCountedNotPermanentlyFailed()
    {
        // The Worker that runs the sweep may not have IParseJobEnqueuer registered.
        // A transient stall must still NOT become a permanent failure on the first blip.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);

        var acted = await new StuckOrderDetectionService(
            db, NullLogger<StuckOrderDetectionService>.Instance, parseEnqueuer: null)
            .RunAsync(Threshold, default);

        acted.Should().Be(1);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.PendingParse);
        order.RequeueCount.Should().Be(1);
        (await db.AuditEvents.SingleAsync(e => e.EntityId == orderId)).Action
            .Should().Be("StuckRequeued");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StuckOrderDetectionService CreateService(
        ProcuLinkDbContext db, IParseJobEnqueuer? enqueuer = null) =>
        new(db, NullLogger<StuckOrderDetectionService>.Instance, enqueuer);

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, string status, int updatedMinutesAgo, int requeueCount = 0)
    {
        var orderId = Guid.NewGuid();
        var updated = DateTime.UtcNow.AddMinutes(-updatedMinutesAgo);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-STUCK",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            RequeueCount = requeueCount,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private sealed class RecordingParseEnqueuer : IParseJobEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }
}
