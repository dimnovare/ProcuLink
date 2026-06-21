using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class OpsControllerTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OpsController Ctrl, Mock<IOpsHealthService> Health,
                    Mock<IOrderService> Orders, Mock<IBackgroundJobClient> Jobs, Guid OrgId)
        Build(ProcuLinkDbContext db)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var health = new Mock<IOpsHealthService>();
        var orders = new Mock<IOrderService>();
        var jobs   = new Mock<IBackgroundJobClient>();

        var ctrl = new OpsController(
            health.Object, tenant.Object, orders.Object,
            jobs.Object, db, NullLogger<OpsController>.Instance);
        return (ctrl, health, orders, jobs, orgId);
    }

    private static PurchaseOrderEntity OrderWithArtifact(Guid orgId, string status)
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-DL",
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

    // ── GET /api/ops/health ───────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_ReturnsMappedDto()
    {
        await using var db = NewDb();
        var (ctrl, health, _, _, orgId) = Build(db);
        health.Setup(h => h.GetHealthAsync(orgId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OpsHealthSummary(
                  ParsingStuck: 1, DeliveringStuck: 0, TransformFailed: 0,
                  DeliveryFailed: 2, DeliveryDeadLetter: 3, RejectedBySupplier: 0,
                  Failed: 0, SlaBreached: 1, OpenExceptions: 4, StuckThresholdMinutes: 30,
                  PendingReview: 7));

        var result = await ctrl.GetHealth(CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<OpsHealthDto>().Subject;
        dto.DeliveryDeadLetter.Should().Be(3);
        dto.OpenExceptions.Should().Be(4);
        dto.PendingReview.Should().Be(7);              // informational manual-review backlog
        dto.TotalProblemOrders.Should().Be(1 + 2 + 3); // sum of order-state counts; PendingReview excluded
    }

    [Fact]
    public void OpsHealthDto_SerialisesPendingReview_AsCamelCaseJson()
    {
        // The frontend reads exactly `pendingReview`. Pin the System.Text.Json camelCase
        // contract so a rename can't silently break the operator dashboard.
        var dto = new OpsHealthDto(
            ParsingStuck: 0, DeliveringStuck: 0, TransformFailed: 0, DeliveryFailed: 0,
            DeliveryDeadLetter: 0, RejectedBySupplier: 0, Failed: 0, SlaBreached: 0,
            OpenExceptions: 0, StuckThresholdMinutes: 30, TotalProblemOrders: 0,
            ActiveWorkers: 0, LastWorkerHeartbeatUtc: null, SecondsSinceWorkerHeartbeat: null,
            WorkerHealthy: false, PendingReview: 5);

        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        json.Should().Contain("\"pendingReview\":5");
    }

    // ── GET /api/ops/dead-letter ──────────────────────────────────────────────

    [Fact]
    public async Task GetDeadLetter_PassesIncludeFailedFlag_AndMapsRows()
    {
        await using var db = NewDb();
        var (ctrl, health, _, _, orgId) = Build(db);
        var orderId = Guid.NewGuid();
        health.Setup(h => h.ListDeadLetterAsync(orgId, true, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<DeadLetterOrder>
              {
                  new(orderId, "PO-1", Guid.NewGuid(), "Acme",
                      OrderStatusConstants.DeliveryDeadLetter, 3, "boom", 503,
                      DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow),
              });

        var result = await ctrl.GetDeadLetter(includeFailed: true, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<IEnumerable<DeadLetterOrderDto>>().Subject.ToList();
        dtos.Should().HaveCount(1);
        dtos[0].LastError.Should().Be("boom");
        dtos[0].DeliveryAttempts.Should().Be(3);
        health.Verify(h => h.ListDeadLetterAsync(orgId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── POST /api/ops/orders/{id}/requeue-delivery ────────────────────────────

    [Fact]
    public async Task RequeueDelivery_FromDeadLetter_FlipsToDelivering_AndEnqueues()
    {
        await using var db = NewDb();
        var (ctrl, _, orders, jobs, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryDeadLetter);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        var persisted = await db.PurchaseOrders.FindAsync(order.Id);
        persisted!.Status.Should().Be(OrderStatusConstants.Delivering);

        // A DeliverOrderJob was enqueued.
        jobs.Verify(j => j.Create(
            It.Is<Job>(job => job.Type == typeof(ProcuLink.Api.Jobs.DeliverOrderJob)),
            It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task RequeueDelivery_FromDeliveryFailed_IsAllowed()
    {
        await using var db = NewDb();
        var (ctrl, _, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryFailed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task RequeueDelivery_WrongStatus_ReturnsBadRequest()
    {
        await using var db = NewDb();
        var (ctrl, _, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.Delivered);
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RequeueDelivery_NoArtifact_ReturnsBadRequest()
    {
        await using var db = NewDb();
        var (ctrl, _, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryDeadLetter);
        order.OutboundArtifacts.Clear();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RequeueDelivery_OrderNotFound_Returns404()
    {
        await using var db = NewDb();
        var (ctrl, _, orders, _, orgId) = Build(db);
        var id = Guid.NewGuid();
        orders.Setup(o => o.GetByIdAsync(orgId, id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Fail("Order not found."));

        var result = await ctrl.RequeueDelivery(id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
