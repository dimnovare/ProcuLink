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

    private static async Task SeedFailedAttemptsAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId, int count)
    {
        for (var i = 1; i <= count; i++)
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
                Channel = "http", Destination = "https://supplier.example",
                Status = "failed", AttemptNumber = i, AttemptedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
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
    public async Task GetHealth_MapsDeliveryHeld_ToDto()
    {
        // The operator dashboard can only show billing-held orders if the count survives the
        // summary → DTO hop. Pin it: a dropped mapping here re-hides the paused POs.
        await using var db = NewDb();
        var (ctrl, health, _, _, orgId) = Build(db);
        health.Setup(h => h.GetHealthAsync(orgId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OpsHealthSummary(
                  ParsingStuck: 0, DeliveringStuck: 0, TransformFailed: 0,
                  DeliveryFailed: 0, DeliveryDeadLetter: 0, RejectedBySupplier: 0,
                  Failed: 0, SlaBreached: 0, OpenExceptions: 0, StuckThresholdMinutes: 30,
                  DeliveryHeld: 4));

        var result = await ctrl.GetHealth(CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<OpsHealthDto>().Subject;
        dto.DeliveryHeld.Should().Be(4);
        dto.TotalProblemOrders.Should().Be(4,
            "held orders count toward the all-clear determination — a paused PO is not 'All clear'");
    }

    [Fact]
    public void OpsHealthDto_SerialisesDeliveryHeld_AsCamelCaseJson()
    {
        // The frontend reads exactly `deliveryHeld`. Pin the camelCase contract like pendingReview.
        var dto = new OpsHealthDto(
            ParsingStuck: 0, DeliveringStuck: 0, TransformFailed: 0, DeliveryFailed: 0,
            DeliveryDeadLetter: 0, RejectedBySupplier: 0, Failed: 0, SlaBreached: 0,
            OpenExceptions: 0, StuckThresholdMinutes: 30, TotalProblemOrders: 2,
            ActiveWorkers: 0, LastWorkerHeartbeatUtc: null, SecondsSinceWorkerHeartbeat: null,
            WorkerHealthy: false, PendingReview: 0, DeliveryHeld: 2);

        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

        json.Should().Contain("\"deliveryHeld\":2");
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
    public async Task RequeueDelivery_FromDeadLetter_LeavesClaimableStatus_ResetsAttempts_AndEnqueues()
    {
        // B2 (lost-order): the escalation endpoint must NOT pre-flip to a fresh 'delivering' — the
        // enqueued DeliverOrderJob's atomic claim REJECTS a fresh 'delivering' row (it only claims
        // ready_to_deliver / delivery_failed / STALE-delivering), so the pre-flip was a benign no-op
        // that stranded the order and silently defeated the requeue. Instead it must leave the order
        // in a CLAIMABLE status and RESET the attempt cap so the operator's dispatch-past-the-cap
        // intent actually reaches the supplier.
        await using var db = NewDb();
        var (ctrl, _, orders, jobs, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryDeadLetter);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        await SeedFailedAttemptsAsync(db, orgId, order.Id, count: 3); // already at the dead-letter cap

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        var persisted = await db.PurchaseOrders.FindAsync(order.Id);
        // NOT 'delivering' — that status is exactly what the claim rejects.
        persisted!.Status.Should().NotBe(OrderStatusConstants.Delivering);
        // A claimable, send-ready idle status so the enqueued DeliverOrderJob's claim SUCCEEDS.
        persisted.Status.Should().BeOneOf(
            OrderStatusConstants.DeliveryFailed, OrderStatusConstants.ReadyToDeliver);

        // Attempt cap reset so a transient failure of the requeued dispatch re-engages the retry queue.
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == order.Id)).Should().Be(0);

        // A DeliverOrderJob was enqueued.
        jobs.Verify(j => j.Create(
            It.Is<Job>(job => job.Type == typeof(ProcuLink.Api.Jobs.DeliverOrderJob)),
            It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task RequeueDelivery_SnapshotsClearedAttemptForensicsIntoAuditPayload()
    {
        // B2 forensics: hard-deleting the prior attempts to reset the cap must NOT lose their
        // diagnostic history. The DeliveryRequeuedByOperator audit event captures each cleared
        // attempt's forensics (attempt #, status, response code, error, body, timestamp) so the
        // dead-letter evidence survives the requeue.
        await using var db = NewDb();
        var (ctrl, _, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryDeadLetter);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var attemptedAt = DateTime.UtcNow.AddMinutes(-5);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = order.Id, OrgId = orgId,
            Channel = "http", Destination = "https://supplier.example/orders",
            Status = "failed", AttemptNumber = 3, AttemptedAt = attemptedAt,
            ResponseCode = 503, ErrorMessage = "Gateway timeout",
            ResponseBody = "upstream connect error",
        });
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RequeueDelivery(order.Id, CancellationToken.None);
        result.Should().BeOfType<AcceptedResult>();

        // Rows are still hard-deleted (cap reset), but their forensics survive in the audit payload.
        (await db.DeliveryAttempts.CountAsync(a => a.OrderId == order.Id)).Should().Be(0);

        var audit = await db.AuditEvents.SingleAsync(
            e => e.EntityId == order.Id && e.Action == "DeliveryRequeuedByOperator");
        var cleared = audit.Payload!.RootElement.GetProperty("clearedAttempts");
        cleared.GetArrayLength().Should().Be(1);
        var a0 = cleared[0];
        a0.GetProperty("attemptNumber").GetInt32().Should().Be(3);
        a0.GetProperty("status").GetString().Should().Be("failed");
        a0.GetProperty("responseCode").GetInt32().Should().Be(503);
        a0.GetProperty("errorMessage").GetString().Should().Be("Gateway timeout");
        a0.GetProperty("responseBody").GetString().Should().Be("upstream connect error");
        a0.GetProperty("attemptedAt").GetDateTime().Should().BeCloseTo(attemptedAt, TimeSpan.FromSeconds(1));
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
