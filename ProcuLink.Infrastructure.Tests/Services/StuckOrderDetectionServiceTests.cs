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
/// pipeline status ('pending_parse' / 'parsing' / 'transforming') past the timeout are
/// RE-ENQUEUED (a transient Worker restart mid-job is recoverable), up to a bounded cap,
/// after which the outcome is leg-aware: a parse-side strand dead-letters as genuinely
/// failed, a 'transforming' one recovers to 're-sendable' ready. Each action writes an
/// audit event. Uses the full ProcuLinkDbContext on InMemory.
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
        // Kept in 'parsing' — the ONLY status the parse guard (ParseStoredFileAsync)
        // actually re-parses. Resetting to 'pending_parse' would make the re-enqueued
        // job skip it as "already processed" and strand it. NOT failed.
        order.Status.Should().Be(OrderStatusConstants.Parsing);
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
    public async Task RunAsync_StuckParsingOrder_StaysPickupEligibleAndReEnqueued_NotStranded()
    {
        // Regression (audit FINDING 2): a stuck 'parsing' order must be recovered to a status
        // the parse guard ACTUALLY picks up. ParseStoredFileAsync only re-parses when
        // Status == "parsing"; resetting to "pending_parse" made the re-enqueued job log
        // "already processed, skipping parse" and return Ok WITHOUT parsing — a silent strand.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);
        var enqueuer = new RecordingParseEnqueuer();

        await CreateService(db, enqueuer).RunAsync(Threshold, default);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        // Pickup-eligible: exactly the literal the parse guard compares against ("parsing").
        order.Status.Should().Be(OrderStatusConstants.Parsing);
        // A fresh parse job was enqueued to actually drive the re-parse.
        enqueuer.Calls.Should().ContainSingle().Which.Should().Be((orderId, order.OrgId));
        // UpdatedAt was bumped out of the stuck window so the next sweep won't double-act.
        order.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-Threshold.TotalMinutes));
    }

    /// <summary>
    /// 'pending_parse' is the C# default on <c>PurchaseOrderEntity.Status</c> and nothing writes it
    /// today — every construction site overrides it before the row is saved. It survives as a
    /// DEFAULT WAITING TO LEAK: the first ingest path that forgets one of those assignments lands an
    /// order in a status with no sweeper, no alert and no UI bucket, which makes it permanently
    /// invisible rather than merely late. Sweeping the status costs nothing while nothing writes it
    /// (the query matches no rows) and converts that silent, permanent loss into the ordinary
    /// requeue path the moment it does.
    ///
    /// <para>The order is re-driven through <c>parsing</c> for the same reason a stalled 'parsing'
    /// strand is: that literal is the only status <c>ParseStoredFileAsync</c> acts on.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_LeakedPendingParseOrder_IsSweptIntoParsingAndReEnqueued_NotStranded()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.PendingParse, updatedMinutesAgo: 45);
        var enqueuer = new RecordingParseEnqueuer();

        var acted = await CreateService(db, enqueuer).RunAsync(Threshold, default);

        acted.Should().Be(1, "an order sitting in pending_parse past the threshold is invisible to " +
                             "every other watchdog, so this sweep is the only thing that can see it");

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Parsing);
        order.RequeueCount.Should().Be(1);
        enqueuer.Calls.Should().ContainSingle().Which.Should().Be((orderId, order.OrgId));

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckRequeued");
    }

    /// <summary>
    /// The trap this packet's obvious one-line version walks into. Adding <c>pending_parse</c> to
    /// the transient set while the requeue branch stays keyed on <c>== Parsing</c> routes a leaked
    /// order into the TRANSFORM recovery — which resets it to <c>ready</c>. An order that has never
    /// been parsed has no lines, so 'ready' offers the operator an empty PO to send to a supplier,
    /// and no parse job is ever enqueued to fill it. Strictly worse than leaving it stranded, and
    /// silent in exactly the same way.
    /// </summary>
    [Fact]
    public async Task RunAsync_LeakedPendingParseOrder_IsNeverRecoveredToReadyLikeATransformStrand()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.PendingParse, updatedMinutesAgo: 45);
        var enqueuer = new RecordingParseEnqueuer();

        await CreateService(db, enqueuer).RunAsync(Threshold, default);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().NotBe(OrderStatusConstants.Ready,
            "an order that has never been parsed has no lines — 'ready' would offer the operator an " +
            "empty PO to send, and the transform-recovery branch enqueues no parse job to fill it");
        enqueuer.Calls.Should().ContainSingle(
            "the parse leg re-drives through a fresh parse job; the transform leg enqueues nothing");
    }

    /// <summary>
    /// Past the requeue budget a parse-side strand dead-letters, and a leaked pending_parse order
    /// is a parse-side strand. The <c>pending_parse → failed</c> edge that implies is declared in
    /// <c>OrderStatusMachine.Transitions</c>.
    /// </summary>
    [Fact]
    public async Task RunAsync_LeakedPendingParseOrderPastRequeueCap_IsDeadLetteredAsFailed()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.PendingParse, updatedMinutesAgo: 45, requeueCount: MaxRequeues);

        var acted = await CreateService(db, new RecordingParseEnqueuer()).RunAsync(Threshold, default);

        acted.Should().Be(1);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Failed);

        var audit = await db.AuditEvents.SingleAsync(e => e.EntityId == orderId);
        audit.Action.Should().Be("StuckTimeout");
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
            // Requeue keeps it in 'parsing' (pickup-eligible), never failed, until the cap.
            o.Status.Should().Be(OrderStatusConstants.Parsing, "requeue {0} should not fail the order", attempt);
            o.RequeueCount.Should().Be(attempt);

            // Re-stall: it is already 'parsing'; just age it past the threshold again.
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
        // pending_parse/parsing/transforming count as "stuck".
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

        // First run requeues (bumps UpdatedAt = now, keeps status 'parsing').
        (await service.RunAsync(Threshold, default)).Should().Be(1);
        // Second run: the order is still 'parsing' but its UpdatedAt was just bumped, so it
        // is OUTSIDE the stuck window (recency guard) and is not acted on again — no
        // double-processing. It re-enters the sweep only if it stalls past the threshold again.
        (await service.RunAsync(Threshold, default)).Should().Be(0);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).RequeueCount.Should().Be(1);
        (await db.AuditEvents.CountAsync(e => e.EntityId == orderId)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoEnqueuerRegistered_ParsingOrderStillRequeuedAndCountedNotPermanentlyFailed()
    {
        // The process that runs the sweep may not have IParseJobEnqueuer registered.
        // A transient stall must still NOT become a permanent failure on the first blip, and
        // must stay in the pickup-eligible 'parsing' status so a later run (or a process that
        // DOES have the enqueuer) can re-drive it.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(db, OrderStatusConstants.Parsing, updatedMinutesAgo: 45);

        var acted = await new StuckOrderDetectionService(
            db, NullLogger<StuckOrderDetectionService>.Instance, parseEnqueuer: null)
            .RunAsync(Threshold, default);

        acted.Should().Be(1);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Parsing);
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
