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
