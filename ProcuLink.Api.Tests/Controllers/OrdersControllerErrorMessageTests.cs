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
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(friendlyMessage, dto.ErrorMessage);
    }

    [Fact]
    public async Task Get_DeliveryFailedOrderWithLatestAttempt_ReturnsAttemptErrorMessage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const string attemptMessage =
            "Supplier delivery config is missing. Add a delivery endpoint before sending this order.";

        await using var db = NewDb();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "missing_config",
            Destination   = "supplier delivery config",
            Status        = "failed",
            AttemptedAt   = DateTime.UtcNow,
            ErrorMessage  = attemptMessage,
        });
        await db.SaveChangesAsync();

        var failedEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-DELIVERY-FAIL",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = "delivery_failed",
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
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(attemptMessage, dto.ErrorMessage);
    }

    [Fact]
    public async Task Get_DeadLetteredOrderWithDeliveryDeadLetteredAudit_ReturnsLastError()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const string lastError =
            "Supplier delivery config is missing. Add a delivery endpoint before sending this order.";

        await using var db = NewDb();
        // DeadLetterAsync writes Action="DeliveryDeadLettered" with payload key "lastError".
        db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = "DeliveryDeadLettered",
            Payload    = JsonDocument.Parse(
                $$$"""{"lastError":"{{{lastError}}}","attemptCount":3}"""),
            CreatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var deadEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-DEAD",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = "delivery_dead_letter",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(deadEntity));

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
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(lastError, dto.ErrorMessage);
    }

    [Fact]
    public async Task Get_DeliveryHeldOrderAfterPriorDeliveryFailure_ReturnsNullErrorMessage()
    {
        // A5 path: a delivery_failed order whose org lapsed on billing is HELD (delivery_failed →
        // delivery_held). The prior failure's audit event AND delivery attempt both still exist —
        // they are real history and are deliberately kept. But a billing PAUSE must never be
        // explained by the PREVIOUS delivery error: that would describe the wrong problem.
        // errorMessage is derived per-status at read time, and delivery_held is not an
        // error-deriving status → null. Pin it so no stale failure text can leak to any consumer.
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = "DeliveryFailed",
            Payload    = JsonDocument.Parse(
                """{"error":"Connection refused by supplier endpoint.","stage":"deliver"}"""),
            CreatedAt  = DateTime.UtcNow.AddMinutes(-5),
        });
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "http",
            Destination   = "https://supplier.example/orders",
            Status        = "failed",
            AttemptedAt   = DateTime.UtcNow.AddMinutes(-5),
            ErrorMessage  = "Connection refused by supplier endpoint.",
        });
        await db.SaveChangesAsync();

        var heldEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-HELD",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = ProcuLink.Core.Constants.OrderStatusConstants.DeliveryHeld,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(heldEntity));

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
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Null(dto.ErrorMessage);
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
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Null(dto.ErrorMessage);
    }
}
