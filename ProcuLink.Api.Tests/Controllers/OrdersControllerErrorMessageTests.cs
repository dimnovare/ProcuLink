using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Controllers;

public class OrdersControllerErrorMessageTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Get_FailedOrderWithParsedFailedAuditEvent_ReturnsErrorMessage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const string friendlyMessage = "No line-table columns detected. We couldn't find recognisable item columns.";

        await using var db = NewDb();
        db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = "ParseFailed",
            Payload    = JsonDocument.Parse(
                $$$"""{"error":"{{{friendlyMessage}}}","stage":"parse","detail":"0 lines parsed"}"""),
            CreatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var failedEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-FAIL",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = "failed",
            SourceFileKey     = $"{orgId}/{orderId}/file.csv",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(failedEntity));

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new OrdersController(
            ordersSvc.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object);

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(friendlyMessage, dto.ErrorMessage);
    }

    [Fact]
    public async Task Get_ReadyOrder_ReturnsNullErrorMessage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();

        var readyEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-READY",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = "ready",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(readyEntity));

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new OrdersController(
            ordersSvc.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object);

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Null(dto.ErrorMessage);
    }
}
