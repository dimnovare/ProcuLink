using FluentAssertions;
using Hangfire;
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
/// Task 4: "Send again" (POST /api/orders/{id}/redeliver) accepted from a parked
/// (delivery_unconfirmed) order — the operator, not the automatic retry loop, is the one who
/// accepts the duplicate-send risk. Also pins that the 400 body is DERIVED from
/// <see cref="OrderStatusMachine.RedeliverableFrom"/> rather than a hardcoded literal, so the
/// sentence can never quietly go stale when the set grows.
/// </summary>
public class OrdersControllerRedeliverTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrgId) Build(ProcuLinkDbContext db)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
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

        return (ctrl, orders, orgId);
    }

    private static PurchaseOrderEntity OrderWithArtifact(Guid orgId, string status)
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

    private static string ReadError(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        return (string)bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)!;
    }

    // The operator's "Send again" on a parked order — the whole point of the park is that a
    // HUMAN, not the retry loop, decides to accept the duplicate risk.
    [Fact]
    public async Task Redeliver_FromDeliveryUnconfirmed_IsAccepted()
    {
        await using var db = NewDb();
        var (ctrl, orders, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.Redeliver(order.Id, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        (await db.PurchaseOrders.SingleAsync(o => o.Id == order.Id)).Status
            .Should().Be(OrderStatusConstants.Delivering);
    }

    // The 400 message must name the statuses that are ACTUALLY redeliverable, not a stale literal —
    // adding delivery_unconfirmed to the set must not leave the sentence quietly lying.
    [Fact]
    public async Task Redeliver_FromInvalidStatus_ErrorMessage_ListsEveryRedeliverableStatus()
    {
        await using var db = NewDb();
        var (ctrl, orders, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.Parsing);
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
        await using var db = NewDb();
        var (ctrl, orders, orgId) = Build(db);
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

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrgId, Guid ActingUserId) Build(
        ProcuLinkDbContext db)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        tenant.SetupGet(t => t.ClerkUserId).Returns(ActingClerkUserId);

        // The acting user resolves to an internal AppUser row via the Clerk sub claim — mirrors how
        // AuditEvent.UserId (a Guid FK) relates to AppUser, since ICurrentTenantService only exposes
        // the Clerk string id, never the internal Guid directly.
        var actingUserId = Guid.NewGuid();
        db.Users.Add(new AppUser
        {
            Id          = actingUserId,
            ClerkUserId = ActingClerkUserId,
            Email       = "operator@example.test",
            CreatedAt   = DateTime.UtcNow,
        });
        db.SaveChanges();

        var orders = new Mock<IOrderService>();

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
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

    // The operator confirms out-of-band (phone/portal) that the supplier DID receive it.
    [Fact]
    public async Task MarkDelivered_FromDeliveryUnconfirmed_SetsDelivered_AndClearsSla()
    {
        await using var db = NewDb();
        var (ctrl, orders, orgId, _) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        db.PurchaseOrders.Add(order);
        db.DeliveryAttempts.Add(UnconfirmedAttempt(orgId, order.Id));
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        var persisted = await db.PurchaseOrders.SingleAsync(o => o.Id == order.Id);
        persisted.Status.Should().Be(OrderStatusConstants.Delivered);
        persisted.DeliveryDueAt.Should().BeNull("a confirmed delivery closes the SLA window");
        persisted.SlaBreached.Should().BeFalse();
    }

    // We never fabricate a supplier ACK we did not observe.
    [Fact]
    public async Task MarkDelivered_LeavesAttemptRowUnconfirmed_AndAuditsTheHumanAssertion()
    {
        await using var db = NewDb();
        var (ctrl, orders, orgId, actingUserId) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        db.PurchaseOrders.Add(order);
        db.DeliveryAttempts.Add(UnconfirmedAttempt(orgId, order.Id));
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        var attempt = await db.DeliveryAttempts.SingleAsync(a => a.OrderId == order.Id);
        attempt.Status.Should().Be(DeliveryAttempt.StatusUnconfirmed,
            "the send's outcome was never observed — only the operator's assertion is new");

        var audit = await db.AuditEvents.SingleAsync(
            e => e.EntityId == order.Id && e.Action == "DeliveryConfirmedManually");
        audit.UserId.Should().Be(actingUserId, "the human who asserted delivery is on the record");
    }

    [Fact]
    public async Task MarkDelivered_FromAnyOtherStatus_Is400()
    {
        await using var db = NewDb();
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
        await using var db = NewDb();
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
        await using var db = NewDb();
        var (ctrl, orders, orgId, _) = Build(db);

        var order = OrderInStatus(orgId, OrderStatusConstants.DeliveryUnconfirmed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        await ctrl.MarkDelivered(order.Id, CancellationToken.None);

        // Mirrors StripeBillingService.ApplyMeterStatusFilter's billable set exactly.
        var billable = await db.PurchaseOrders.CountAsync(o =>
            o.OrgId == orgId &&
            (o.Status == OrderStatusConstants.Delivered || o.Status == OrderStatusConstants.RejectedBySupplier));

        billable.Should().Be(1);
    }
}
