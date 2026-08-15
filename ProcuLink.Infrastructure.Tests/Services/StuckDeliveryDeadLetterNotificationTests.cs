using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// B-13 (P1), second half — <see cref="StuckDeliveryDetectionService"/> is the SECOND dead-letter
/// writer in the system, and it was as silent as the first.
///
/// <para>
/// The audit that raised B-13 named <c>DeliveryService.DeadLetterAsync</c> as "the entire
/// dead-letter path". It is not: an order that strands in <c>delivering</c> past its re-drive budget
/// is dead-lettered here instead, under a different audit action (<c>StuckDeliveryDeadLettered</c>).
/// Wiring the notification to only one of the two writers would have left a whole class of
/// undeliverable POs silent while the fix looked complete — the failure mode of a guard that
/// encodes only the shape of the defect that prompted it.
/// </para>
///
/// <para>
/// <b>Anti-vacuity.</b> <see cref="RunAsync_StuckOrderWithBudgetLeft_IsReDrivenAndNotNotified"/> is
/// the paired control: it drives the same sweep, with the same recording fake, down the re-drive
/// branch and asserts the recorder observed NOTHING while the re-drive enqueuer observed a call.
/// That pins that the sweep really ran and really reached the recorder's neighbourhood, so the
/// empty result is a real negative rather than a test that never executed the code.
/// </para>
/// </summary>
public class StuckDeliveryDeadLetterNotificationTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    // Mirrors StuckDeliveryDetectionService.MaxRequeues.
    private const int MaxRequeues = 2;

    [Fact]
    public async Task RunAsync_StuckOrderPastRequeueCap_IsDeadLetteredAndNotifiesTheCustomer()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var trigger = new RecordingIntegrationTriggerService();

        await CreateService(db, trigger: trigger).RunAsync(Threshold, default);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryDeadLetter);

        var events = trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered);
        events.Should().HaveCount(1,
            "a PO stranded past its re-drive budget is just as undeliverable as one that spent its retry budget");
        events[0].OrgId.Should().Be(order.OrgId);
    }

    /// <summary>
    /// Anti-vacuity control AND the under-budget negative: a stall with re-drive budget left is a
    /// transient stall, not a dead order, and must not be announced as terminal.
    /// </summary>
    [Fact]
    public async Task RunAsync_StuckOrderWithBudgetLeft_IsReDrivenAndNotNotified()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: 0);
        var trigger = new RecordingIntegrationTriggerService();
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer, trigger).RunAsync(Threshold, default);

        acted.Should().Be(1, "the sweep really did act on this order");
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering, "a re-drive is not a terminal state");

        // ANTI-VACUITY: the sweep reached the post-commit fan-out block on this run — the re-drive
        // enqueuer sits in the sibling block and was called. So the empty recorder below is a real
        // negative, not a test that exited before any notification code could run.
        enqueuer.Calls.Should().ContainSingle();

        trigger.Events.Should().BeEmpty(
            "an order that is being re-driven has not failed — announcing it dead would have the "
          + "customer re-route a PO that the next re-drive delivers");
    }

    /// <summary>
    /// Idempotency. The sweep selects strictly on <c>Status == delivering</c>, so once the terminal
    /// status is committed the order leaves the scan set permanently and no later sweep can notify
    /// again. This is why the enqueue is ordered AFTER <c>SaveChangesAsync</c>: emitting before the
    /// commit would leave a crashed sweep's order still 'delivering', re-selected on the next pass,
    /// and notified a second time.
    /// </summary>
    [Fact]
    public async Task RunAsync_RepeatedSweeps_NotifyOnlyOncePerOrder()
    {
        await using var db = CreateDb();
        await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var trigger = new RecordingIntegrationTriggerService();
        var service = CreateService(db, trigger: trigger);

        await service.RunAsync(Threshold, default);
        await service.RunAsync(Threshold, default);
        await service.RunAsync(Threshold, default);

        trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered).Should().HaveCount(1,
            "a dead-lettered order is no longer 'delivering', so later sweeps cannot re-select it");
    }

    [Fact]
    public async Task RunAsync_ManyStuckOrdersAcrossOrgs_NotifiesEachOwningOrgSeparately()
    {
        await using var db = CreateDb();
        var first = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var second = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 90, deliveryRequeueCount: MaxRequeues);
        var trigger = new RecordingIntegrationTriggerService();

        await CreateService(db, trigger: trigger).RunAsync(Threshold, default);

        var orgs = await db.PurchaseOrders
            .Where(o => o.Id == first || o.Id == second)
            .Select(o => o.OrgId)
            .ToListAsync();

        var events = trigger.EventsOf(IntegrationEventTypes.OrderDeadLettered);
        events.Should().HaveCount(2);
        // This sweep is cross-tenant; each event must carry its own order's org or one customer
        // would be told about another customer's PO.
        events.Select(e => e.OrgId).Should().BeEquivalentTo(orgs);
    }

    [Fact]
    public async Task RunAsync_WithNoTriggerRegistered_StillDeadLettersAndDoesNotThrow()
    {
        // The seam is optional so the existing positional constructors keep compiling. A host
        // without it must still reach the terminal state — losing the notification must never cost
        // the status transition.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);

        var act = async () => await CreateService(db).RunAsync(Threshold, default);

        await act.Should().NotThrowAsync();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter);
    }

    [Fact]
    public async Task RunAsync_WhenNotificationThrows_TheOrderStaysDeadLettered()
    {
        // The order IS dead — that fact is already committed. A fan-out failure must not roll it
        // back or abort the sweep for the remaining orgs.
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);

        var act = async () => await CreateService(db, trigger: new ThrowingIntegrationTriggerService())
            .RunAsync(Threshold, default);

        await act.Should().NotThrowAsync();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryDeadLetter);
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

    private sealed class ThrowingIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct) =>
            throw new InvalidOperationException("subscription fan-out unavailable");
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

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StuckDeliveryDetectionService CreateService(
        ProcuLinkDbContext db,
        IRetryDeliveryEnqueuer? enqueuer = null,
        IIntegrationTriggerService? trigger = null) =>
        new(db, NullLogger<StuckDeliveryDetectionService>.Instance, enqueuer, trigger);

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db,
        string status,
        int updatedMinutesAgo,
        int deliveryRequeueCount = 0)
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
            DeliveryRequeueCount = deliveryRequeueCount,
            CreatedAt = updated,
            UpdatedAt = updated,
        });
        await db.SaveChangesAsync();
        return orderId;
    }
}
