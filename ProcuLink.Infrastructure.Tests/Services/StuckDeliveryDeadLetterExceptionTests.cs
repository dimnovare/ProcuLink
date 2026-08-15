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
/// <see cref="StuckDeliveryDetectionService"/> is the SECOND dead-letter writer in the system, and
/// it was the only one that did not open an exception row.
///
/// <para>
/// <c>DeliveryService.DeadLetterAsync</c> calls <c>SafeReconcileExceptionsAsync</c>, so ITS
/// dead-letters raise the <c>dead_letter</c> problem — the single <c>critical</c> severity in
/// <c>OrderExceptionService.ProblemFor</c>. An order dead-lettered by this sweep instead reached the
/// same terminal, undeliverable status while <c>GET /api/exceptions</c> and the in-app inbox showed
/// nothing at all. The order was as dead; only the operator's view of it differed, which is the
/// worse half: a terminal PO that looks fine is one nobody re-routes.
/// </para>
///
/// <para>
/// <b>Anti-vacuity.</b> <see cref="RunAsync_StuckOrderWithBudgetLeft_OpensNoDeadLetterException"/>
/// drives the SAME harness — same real <see cref="OrderExceptionService"/> over the same context —
/// down the re-drive branch and asserts no row appears, while proving the sweep both acted on the
/// order and reached the post-commit block (the re-drive enqueuer, which sits beside the reconcile
/// call, recorded its call). The positive test above it opens a row through that identical harness,
/// so an empty result here is a real negative rather than a harness that cannot write at all.
/// </para>
/// </summary>
public class StuckDeliveryDeadLetterExceptionTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    // Mirrors StuckDeliveryDetectionService.MaxRequeues.
    private const int MaxRequeues = 2;

    private const string DeadLetterCode = "dead_letter";

    [Fact]
    public async Task RunAsync_StuckOrderPastRequeueCap_OpensTheCriticalDeadLetterException()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);

        await CreateService(db, exceptions: new OrderExceptionService(db)).RunAsync(Threshold, default);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryDeadLetter);

        var rows = await db.OrderExceptions.Where(e => e.OrderId == orderId).ToListAsync();
        rows.Should().ContainSingle(e => e.Code == DeadLetterCode,
            "an order stranded past its re-drive budget is terminal and undeliverable, and the "
          + "exception list is where an operator finds out");

        var deadLetter = rows.Single(e => e.Code == DeadLetterCode);
        deadLetter.State.Should().Be("open");
        deadLetter.Severity.Should().Be("critical");
        deadLetter.Stage.Should().Be("Deliver");
        // Cross-tenant sweep: the row must land on the order's OWN org or it surfaces in the wrong
        // workspace's exception list.
        deadLetter.OrgId.Should().Be(order.OrgId);
    }

    /// <summary>
    /// Anti-vacuity control AND the under-budget negative: a stall with re-drive budget left is
    /// transient, not terminal, so no critical problem may be raised for it.
    /// </summary>
    [Fact]
    public async Task RunAsync_StuckOrderWithBudgetLeft_OpensNoDeadLetterException()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: 0);
        var enqueuer = new RecordingRetryEnqueuer();

        var acted = await CreateService(db, enqueuer, new OrderExceptionService(db))
            .RunAsync(Threshold, default);

        acted.Should().Be(1, "the sweep really did act on this order");
        (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering, "a re-drive is not a terminal state");

        // ANTI-VACUITY: the sweep reached the post-commit block on this run — the re-drive enqueuer
        // sits beside the reconcile call and was called. So the empty result below is a real
        // negative, not a run that exited before any reconcile code could execute.
        enqueuer.Calls.Should().ContainSingle();

        (await db.OrderExceptions.Where(e => e.OrderId == orderId).ToListAsync())
            .Should().NotContain(e => e.Code == DeadLetterCode,
                "flagging a re-driven order as dead-lettered would have the operator re-route a PO "
              + "that the next re-drive delivers");
    }

    /// <summary>
    /// Reconcile is idempotent by suppression: it never opens a second row for a code that already
    /// has an open one. Safe on re-entry, so a later reconcile from any other writer (a status
    /// touch, an operator action) cannot stack duplicate critical rows on the same order.
    /// </summary>
    [Fact]
    public async Task RunAsync_ThenAnotherReconcile_LeavesExactlyOneOpenDeadLetterRow()
    {
        await using var db = CreateDb();
        var orderId = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var exceptions = new OrderExceptionService(db);

        await CreateService(db, exceptions: exceptions).RunAsync(Threshold, default);

        var orgId = (await db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).OrgId;
        await exceptions.ReconcileAsync(orgId, orderId, default);
        await exceptions.ReconcileAsync(orgId, orderId, default);

        (await db.OrderExceptions.Where(e => e.OrderId == orderId && e.Code == DeadLetterCode).ToListAsync())
            .Should().HaveCount(1);
    }

    [Fact]
    public async Task RunAsync_ManyStuckOrdersAcrossOrgs_OpensARowForEachOwningOrg()
    {
        await using var db = CreateDb();
        var first = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var second = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 90, deliveryRequeueCount: MaxRequeues);

        await CreateService(db, exceptions: new OrderExceptionService(db)).RunAsync(Threshold, default);

        var orders = await db.PurchaseOrders
            .Where(o => o.Id == first || o.Id == second)
            .Select(o => new { o.Id, o.OrgId })
            .ToListAsync();

        var rows = await db.OrderExceptions.Where(e => e.Code == DeadLetterCode).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(e => (e.OrderId, e.OrgId))
            .Should().BeEquivalentTo(orders.Select(o => (o.Id, o.OrgId)));
    }

    [Fact]
    public async Task RunAsync_WithNoExceptionServiceRegistered_StillDeadLettersAndDoesNotThrow()
    {
        // The seam is optional so the existing positional test constructors keep compiling. A host
        // without it must still reach the terminal state — losing the exception row must never cost
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
    public async Task RunAsync_WhenReconcileThrows_TheOrderStaysDeadLetteredAndLaterOrgsAreStillReconciled()
    {
        // Exception generation is operational observability data and must never fail the parent
        // operation — the same contract DeliveryService.SafeReconcileExceptionsAsync holds. And
        // because this sweep is cross-tenant, one org's failure must not abandon the rest.
        await using var db = CreateDb();
        var poisoned = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 45, deliveryRequeueCount: MaxRequeues);
        var healthy = await SeedOrderAsync(
            db, OrderStatusConstants.Delivering, updatedMinutesAgo: 90, deliveryRequeueCount: MaxRequeues);

        var exceptions = new ThrowingForOneOrderExceptionService(new OrderExceptionService(db), poisoned);
        var act = async () => await CreateService(db, exceptions: exceptions).RunAsync(Threshold, default);

        await act.Should().NotThrowAsync();

        var statuses = await db.PurchaseOrders
            .Where(o => o.Id == poisoned || o.Id == healthy)
            .Select(o => o.Status)
            .ToListAsync();
        statuses.Should().AllBe(OrderStatusConstants.DeliveryDeadLetter);

        (await db.OrderExceptions.Where(e => e.Code == DeadLetterCode).ToListAsync())
            .Should().ContainSingle(e => e.OrderId == healthy,
                "a throw while reconciling one org's order must not abandon the remaining orgs");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws for one order id, delegates for every other.</summary>
    private sealed class ThrowingForOneOrderExceptionService : IOrderExceptionService
    {
        private readonly IOrderExceptionService _inner;
        private readonly Guid _poisoned;

        public ThrowingForOneOrderExceptionService(IOrderExceptionService inner, Guid poisoned)
        {
            _inner = inner;
            _poisoned = poisoned;
        }

        public Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
            orderId == _poisoned
                ? throw new InvalidOperationException("exception store unavailable")
                : _inner.ReconcileAsync(orgId, orderId, ct);

        public Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct) =>
            _inner.ListAsync(orgId, state, ct);

        public Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
            _inner.ListForOrderAsync(orgId, orderId, ct);

        public Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct) =>
            _inner.ResolveAsync(orgId, exceptionId, ct);

        public Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct) =>
            _inner.IgnoreAsync(orgId, exceptionId, ct);
    }

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StuckDeliveryDetectionService CreateService(
        ProcuLinkDbContext db,
        IRetryDeliveryEnqueuer? enqueuer = null,
        IOrderExceptionService? exceptions = null) =>
        new(db, NullLogger<StuckDeliveryDetectionService>.Instance, enqueuer, integrationTrigger: null, exceptions);

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
