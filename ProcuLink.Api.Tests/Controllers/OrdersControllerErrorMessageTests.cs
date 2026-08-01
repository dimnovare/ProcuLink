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
using ProcuLink.Core.Services.Delivery;
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

    // A parked order MUST explain itself. Without this the operator sees a status they've never
    // seen before and no sentence telling them what happened or which action to take.
    [Fact]
    public async Task Get_ParkedOrder_ReturnsTheUnconfirmedExplanation()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        // The exact sentence DeliveryService.BuildUnconfirmedMessage writes to attempt.ErrorMessage —
        // pinned on stable substrings only (park copy may be reworded; see the task brief).
        const string parkMessage =
            "Delivery unconfirmed. We may have sent this order, but lost the connection before the "
          + "supplier confirmed it, and email cannot tell us whether it arrived. Check with the "
          + "supplier, then either send it again or mark it delivered.";

        await using var db = NewDb();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "email",
            Destination   = "supplier@example.test",
            Status        = DeliveryAttempt.StatusUnconfirmed,
            AttemptedAt   = DateTime.UtcNow,
            ErrorMessage  = parkMessage,
        });
        await db.SaveChangesAsync();

        var parkedEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-PARKED",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = ProcuLink.Core.Constants.OrderStatusConstants.DeliveryUnconfirmed,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(parkedEntity));

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
        Assert.NotNull(dto.ErrorMessage); // a parked order must tell the operator what happened
        Assert.Contains("unconfirmed", dto.ErrorMessage);
        Assert.Contains("mark it delivered", dto.ErrorMessage); // names the action available to them
    }

    // The park sentence must not be pre-empted by an unrelated earlier episode. An order can carry
    // a surviving ParseFailed (or a DeliveryDeadLettered from an ops requeue → crash → park), and
    // the audit-payload branch cannot supply the park sentence anyway — that payload uses key
    // "detail", not "error"/"lastError" — but it CAN win the assignment and show the operator a
    // stale parse error on a parked order.
    [Fact]
    public async Task Get_ParkedOrder_WithStaleParseFailedAudit_StillReturnsTheParkSentence()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const string parkMessage =
            "Delivery unconfirmed. We may have sent this order, but lost the connection before the "
          + "supplier confirmed it, and email cannot tell us whether it arrived. Check with the "
          + "supplier, then either send it again or mark it delivered.";
        const string staleParseError = "No line-table columns detected.";

        await using var db = NewDb();

        // A real earlier episode: this order failed to parse once, was fixed, and went on to be
        // transformed and delivered. The audit row survives — it is an immutable log.
        db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = "ParseFailed",
            Payload    = JsonDocument.Parse($$$"""{"error":"{{{staleParseError}}}","stage":"parse"}"""),
            CreatedAt  = DateTime.UtcNow.AddDays(-1),
        });
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "email",
            Destination   = "supplier@example.test",
            Status        = DeliveryAttempt.StatusUnconfirmed,
            AttemptedAt   = DateTime.UtcNow,
            ErrorMessage  = parkMessage,
        });
        await db.SaveChangesAsync();

        var parkedEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-PARKED-STALE",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = ProcuLink.Core.Constants.OrderStatusConstants.DeliveryUnconfirmed,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(parkedEntity));

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
        Assert.NotNull(dto.ErrorMessage);
        Assert.DoesNotContain(staleParseError, dto.ErrorMessage);
        Assert.Contains("mark it delivered", dto.ErrorMessage);
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

    // ── WP-19 recovery: the cause, machine-readable ───────────────────────────
    //
    // ErrorMessage is prose, and prose is all the API used to carry. A client that wanted to offer
    // the RIGHT recovery control — "update the credentials" vs "correct the address" vs "wait out
    // the rate limit" — had to regex "(HTTP 401)" out of an English sentence, i.e. mirror the
    // backend's classification table in another language in another repository. These two fields
    // are the contract instead, so the copy stays free to improve.

    [Fact]
    public async Task Get_DeliveryFailedOrder_CarriesTheCauseAndTheWait_FromTheNEWESTAttempt()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();

        // An earlier episode with a DIFFERENT cause. If the read picks any attempt but the newest,
        // the operator is offered the recovery for a problem that has already been fixed.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "http",
            Destination   = "https://supplier.example/orders",
            Status        = DeliveryAttempt.StatusFailed,
            AttemptedAt   = DateTime.UtcNow.AddMinutes(-10),
            ResponseCode  = 401,
            ErrorMessage  = "The supplier's endpoint refused our credentials (HTTP 401).",
        });
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id                = Guid.NewGuid(),
            OrgId             = orgId,
            OrderId           = orderId,
            AttemptNumber     = 2,
            Channel           = "http",
            Destination       = "https://supplier.example/orders",
            Status            = DeliveryAttempt.StatusFailed,
            AttemptedAt       = DateTime.UtcNow,
            ResponseCode      = 429,
            RetryAfterSeconds = 90,
            ErrorMessage      = "The supplier's endpoint asked us to slow down (HTTP 429).",
        });
        await db.SaveChangesAsync();

        var failedEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-RATE-LIMITED",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = "delivery_failed",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var dto = await GetDtoAsync(db, orgId, orderId, failedEntity);

        Assert.Equal(SupplierFailureCause.RateLimited, dto.FailureCause);
        Assert.Equal(90, dto.RetryAfterSeconds);
    }

    [Fact]
    public async Task Get_DeliveredOrder_CarriesNoCauseAndNoWait()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();

        // The delivery that failed before it eventually succeeded is real history and stays. But a
        // settled order is not in a failure state, so offering its old cause would put a "fix your
        // credentials" control on an order that is done.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id                = Guid.NewGuid(),
            OrgId             = orgId,
            OrderId           = orderId,
            AttemptNumber     = 1,
            Channel           = "http",
            Destination       = "https://supplier.example/orders",
            Status            = DeliveryAttempt.StatusFailed,
            AttemptedAt       = DateTime.UtcNow.AddMinutes(-10),
            ResponseCode      = 429,
            RetryAfterSeconds = 90,
        });
        await db.SaveChangesAsync();

        var deliveredEntity = new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-DELIVERED",
            OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency          = "EUR",
            Status            = ProcuLink.Core.Constants.OrderStatusConstants.Delivered,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var dto = await GetDtoAsync(db, orgId, orderId, deliveredEntity);

        Assert.Null(dto.FailureCause);
        Assert.Null(dto.RetryAfterSeconds);
    }

    /// <summary>
    /// Builds the controller the way every test in this fixture does and returns the mapped DTO.
    /// </summary>
    private static async Task<OrderDto> GetDtoAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, PurchaseOrderEntity entity)
    {
        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

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

        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<OrderDto>(ok.Value);
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
