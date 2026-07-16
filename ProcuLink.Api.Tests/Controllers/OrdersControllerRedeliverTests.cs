using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Shared harness for the two parked-order operator endpoints.
/// <para>
/// The controller's DbContext is built SEPARATELY from the one that seeds, so its change tracker
/// starts empty — a real scoped per-request context. The mocked <c>IOrderService.GetByIdAsync</c>
/// then returns a DETACHED entity, because that is the only thing the real service can return:
/// <c>OrderQueryService.GetByIdAsync</c> reads through <c>.AsNoTracking()</c>.
/// </para>
/// <para>
/// This is load-bearing, not ceremony. Handing the controller a TRACKED instance (the seeded object
/// itself) makes <c>SaveChangesAsync</c> persist mutations that production silently discards, so
/// "it persisted" passes against a value the real service never produces — which is exactly how a
/// mark-delivered endpoint that never moved the order shipped green.
/// </para>
/// </summary>
internal static class ParkedOrderTestHarness
{
    internal static DbContextOptions<ProcuLinkDbContext> NewOptions() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    /// <summary>
    /// Mirrors <c>OrderQueryService.GetByIdAsync</c>'s read (AsNoTracking + the same Includes), so
    /// the mock hands back what production hands back: a detached POCO.
    /// </summary>
    internal static async Task<PurchaseOrderEntity> DetachedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var entity = await db.PurchaseOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.OutboundArtifacts)
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync();

        entity.Should().NotBeNull("the harness must seed the order before the controller reads it");
        db.Entry(entity!).State.Should().Be(EntityState.Detached,
            "production reads orders through AsNoTracking — a tracked instance would let this test "
            + "prove a persistence the real endpoint never performs");
        return entity!;
    }

    internal static OrdersController NewController(
        ProcuLinkDbContext db, IOrderService orders, ICurrentTenantService tenant, IBackgroundJobClient jobs) =>
        new(
            orders,
            tenant,
            jobs,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

    internal static PurchaseOrderEntity OrderWithArtifact(Guid orgId, string status)
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-REDELIVER",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = status,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
                        Format = "xml", FileKey = "k", CreatedAt = DateTime.UtcNow },
            },
        };
    }
}

/// <summary>
/// Task 4: "Send again" (POST /api/orders/{id}/redeliver) accepted from a parked
/// (delivery_unconfirmed) order — the operator, not the automatic retry loop, is the one who
/// accepts the duplicate-send risk. Also pins that the 400 body is DERIVED from
/// <see cref="OrderStatusMachine.RedeliverableFrom"/> rather than a hardcoded literal, so the
/// sentence can never quietly go stale when the set grows.
/// <para>
/// Scope: this endpoint VALIDATES and ENQUEUES; it does not deliver. The status flip to
/// 'delivering' is the delivery claim, made inside DeliveryService by the enqueued job — proven
/// against the real claim in DeliveryServiceUnconfirmedParkTests.OperatorReSend_FromParkedOrder_ActuallyDispatches.
/// Asserting that transition here would only re-prove the mock.
/// </para>
/// </summary>
public class OrdersControllerRedeliverTests
{
    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Mock<IBackgroundJobClient> Jobs, Guid OrgId) Build(
        ProcuLinkDbContext db)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();
        var jobs   = new Mock<IBackgroundJobClient>();

        return (ParkedOrderTestHarness.NewController(db, orders.Object, tenant.Object, jobs.Object), orders, jobs, orgId);
    }

    private static string ReadError(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        return (string)bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)!;
    }

    // The operator's "Send again" on a parked order — the whole point of the park is that a
    // HUMAN, not the retry loop, decides to accept the duplicate risk.
    [Fact]
    public async Task Redeliver_FromDeliveryUnconfirmed_IsAcceptedAndEnqueuesTheSend()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, jobs, orgId) = Build(db);

        var order = ParkedOrderTestHarness.OrderWithArtifact(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await using (var seed = new ProcuLinkDbContext(options))
        {
            seed.PurchaseOrders.Add(order);
            await seed.SaveChangesAsync();
        }

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(
                  await ParkedOrderTestHarness.DetachedOrderAsync(db, orgId, order.Id)));

        var result = await ctrl.Redeliver(order.Id, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        // The 202 is only honest if a send was actually queued (Hangfire's Enqueue<T> extension
        // bottoms out in IBackgroundJobClient.Create).
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()), Times.Once,
            "an accepted Send again that queues no job would tell the operator it sent something it never sent");

        // The order must stay parked until the job's atomic claim moves it: a pre-flip to a FRESH
        // 'delivering' is precisely what the claim's reclaim-window guard REJECTS, which would
        // strand the order and silently defeat the operator's send (the B2 lost-order trap).
        (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status
            .Should().Be(OrderStatusConstants.DeliveryUnconfirmed);
    }

    // The 400 message must name the statuses that are ACTUALLY redeliverable, not a stale literal —
    // adding delivery_unconfirmed to the set must not leave the sentence quietly lying.
    [Fact]
    public async Task Redeliver_FromInvalidStatus_ErrorMessage_ListsEveryRedeliverableStatus()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, _, orgId) = Build(db);

        var order = ParkedOrderTestHarness.OrderWithArtifact(orgId, OrderStatusConstants.Parsing);
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.Redeliver(order.Id, CancellationToken.None);

        var error = ReadError(result);
        foreach (var status in OrderStatusMachine.RedeliverableFrom)
            error.Should().Contain(status, "the operator must be told every status they can redeliver from");
    }

    [Fact]
    public async Task Redeliver_OrderNotFound_Returns404()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, _, orgId) = Build(db);
        var id = Guid.NewGuid();
        orders.Setup(o => o.GetByIdAsync(orgId, id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Fail("Order not found."));

        var result = await ctrl.Redeliver(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}

/// <summary>
/// Task 5: POST /api/orders/{id}/mark-delivered — the operator's out-of-band confirmation that a
/// parked (delivery_unconfirmed) order DID reach the supplier. Closes the order truthfully without
/// re-sending it. Never fabricates an observed outcome: the delivery ATTEMPT row stays
/// 'unconfirmed' — only the operator's assertion is new, and that assertion is audited under its
/// own action name with the acting user. Same file as <see cref="OrdersControllerRedeliverTests"/>
/// per the task brief (both endpoints live in the same controller region).
/// </summary>
public class OrdersControllerMarkDeliveredTests
{
    private const string ActingClerkUserId = "user_test_operator";

    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrgId, Guid ActingUserId) Build(
        ProcuLinkDbContext db, bool seedActingUser = true)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        tenant.SetupGet(t => t.ClerkUserId).Returns(ActingClerkUserId);

        // The acting user resolves to an internal AppUser row via the Clerk sub claim — mirrors how
        // AuditEvent.UserId (a Guid FK) relates to AppUser, since ICurrentTenantService only exposes
        // the Clerk string id, never the internal Guid directly.
        var actingUserId = Guid.NewGuid();
        if (seedActingUser)
        {
            db.Users.Add(new AppUser
            {
                Id          = actingUserId,
                ClerkUserId = ActingClerkUserId,
                Email       = "operator@example.test",
                CreatedAt   = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var orders = new Mock<IOrderService>();
        var ctrl   = ParkedOrderTestHarness.NewController(db, orders.Object, tenant.Object, new Mock<IBackgroundJobClient>().Object);

        return (ctrl, orders, orgId, actingUserId);
    }

    private static PurchaseOrderEntity OrderInStatus(Guid orgId, string status) => new()
    {
        Id                = Guid.NewGuid(),
        OrgId             = orgId,
        SupplierId        = Guid.NewGuid(),
        PoNumber          = "PO-MARK-DELIVERED",
        OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
        Currency          = "EUR",
        Status            = status,
        CreatedAt         = DateTime.UtcNow,
        UpdatedAt         = DateTime.UtcNow,
        // An open SLA window that a confirmed delivery must close.
        DeliveryDueAt     = DateTime.UtcNow.AddDays(2),
        SlaBreached       = false,
        Lines             = new List<PurchaseOrderLineEntity>(),
        OutboundArtifacts = new List<OutboundArtifact>(),
    };

    private static DeliveryAttempt UnconfirmedAttempt(Guid orgId, Guid orderId) => new()
    {
        Id            = Guid.NewGuid(),
        OrderId       = orderId,
        OrgId         = orgId,
        Channel       = "email",
        Destination   = "supplier@example.test",
        Status        = DeliveryAttempt.StatusUnconfirmed,
        AttemptNumber = 1,
        AttemptedAt   = DateTime.UtcNow,
        ErrorMessage  = "Delivery unconfirmed. We may have sent this order, but lost the connection "
                      + "before the supplier confirmed it, and email cannot tell us whether it "
                      + "arrived. Check with the supplier, then either send it again or mark it delivered.",
    };

    /// <summary>
    /// Seeds through a separate context and returns the DETACHED order the real service would hand
    /// the controller. See <see cref="ParkedOrderTestHarness"/> for why that detachment is the
    /// whole point.
    /// </summary>
    private static async Task<PurchaseOrderEntity> SeedAndDetachAsync(
        DbContextOptions<ProcuLinkDbContext> options, ProcuLinkDbContext db,
        Mock<IOrderService> orders, Guid orgId, PurchaseOrderEntity order, bool withAttempt = true)
    {
        await using (var seed = new ProcuLinkDbContext(options))
        {
            seed.PurchaseOrders.Add(order);
            if (withAttempt)
                seed.DeliveryAttempts.Add(UnconfirmedAttempt(orgId, order.Id));
            await seed.SaveChangesAsync();
        }

        var detached = await ParkedOrderTestHarness.DetachedOrderAsync(db, orgId, order.Id);
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(detached));
        return detached;
    }

    // The operator confirms out-of-band (phone/portal) that the supplier DID receive it.
    [Fact]
    public async Task MarkDelivered_FromDeliveryUnconfirmed_SetsDelivered_AndClearsSla()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, _) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await SeedAndDetachAsync(options, db, orders, orgId, order);

        var result = await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        var persisted = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        persisted.Status.Should().Be(OrderStatusConstants.Delivered);
        persisted.DeliveryDueAt.Should().BeNull("a confirmed delivery closes the SLA window");
        persisted.SlaBreached.Should().BeFalse();
    }

    // We never fabricate a supplier ACK we did not observe.
    [Fact]
    public async Task MarkDelivered_LeavesAttemptRowUnconfirmed_AndAuditsTheHumanAssertion()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, actingUserId) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await SeedAndDetachAsync(options, db, orders, orgId, order);

        await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        var attempt = await db.DeliveryAttempts.AsNoTracking().SingleAsync(a => a.OrderId == order.Id);
        attempt.Status.Should().Be(DeliveryAttempt.StatusUnconfirmed,
            "the send's outcome was never observed — only the operator's assertion is new");

        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(
            e => e.EntityId == order.Id && e.Action == "DeliveryConfirmedManually");
        audit.UserId.Should().Be(actingUserId, "the human who asserted delivery is on the record");
        AuditPayloadActor(audit).Should().Be(ActingClerkUserId);
    }

    // The soft-degrade branch: an operator with no AppUser row (an auth-plumbing gap — a Clerk user
    // whose local row was never provisioned) must NOT be blocked from resolving a parked order.
    // But the null FK must not erase WHO acted: without the Clerk id in the payload, this event
    // becomes shape-identical to the SYSTEM-generated park event (UserId null, same entity) — a
    // record asserting a human act with no human attached. The payload actor keeps a null FK
    // meaning "FK unresolved" rather than "actor unknown".
    [Fact]
    public async Task MarkDelivered_WithNoAppUserRow_StillSucceeds_AndStillNamesTheActor()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, _) = Build(db, seedActingUser: false);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await SeedAndDetachAsync(options, db, orders, orgId, order);

        var result = await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>(
            "an auth-plumbing gap must never strand a parked order the operator can resolve");
        (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status
            .Should().Be(OrderStatusConstants.Delivered);

        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "DeliveryConfirmedManually");
        audit.UserId.Should().BeNull("no AppUser row exists to point the FK at");
        AuditPayloadActor(audit).Should().Be(ActingClerkUserId,
            "a null FK must degrade to 'FK unresolved', never to 'a human acted and we cannot say who'");
    }

    [Fact]
    public async Task MarkDelivered_FromAnyOtherStatus_Is400()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, _) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryFailed);
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MarkDelivered_ForAnotherOrgsOrder_Is404()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, _) = Build(db);
        var id = Guid.NewGuid();
        orders.Setup(o => o.GetByIdAsync(orgId, id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Fail("Order not found."));

        var result = await ctrl.MarkDelivered(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // Billing consequence: metering is a status query, so this is what makes the order chargeable.
    [Fact]
    public async Task MarkDelivered_MakesOrderBillable()
    {
        var options = ParkedOrderTestHarness.NewOptions();
        await using var db = new ProcuLinkDbContext(options);
        var (ctrl, orders, orgId, _) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await SeedAndDetachAsync(options, db, orders, orgId, order, withAttempt: false);

        await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        // Mirrors StripeBillingService.ApplyMeterStatusFilter's billable set exactly.
        var billable = await db.PurchaseOrders.CountAsync(o =>
            o.OrgId == orgId &&
            (o.Status == OrderStatusConstants.Delivered || o.Status == OrderStatusConstants.RejectedBySupplier));

        billable.Should().Be(1);
    }

    private static string? AuditPayloadActor(AuditEvent audit) =>
        audit.Payload!.RootElement.TryGetProperty("actorClerkUserId", out var el) ? el.GetString() : null;
}
