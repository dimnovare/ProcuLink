using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Tokenizing;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// B2 (lost-order), Retry leg: <c>POST /api/orders/{id}/retry-delivery</c> must NOT pre-flip the
/// order to a fresh <c>delivering</c>.
///
/// <para><c>RetryDeliveryAsync</c>'s atomic claim only accepts <c>delivery_failed</c> /
/// <c>ready_to_deliver</c> / a STALE <c>delivering</c> (UpdatedAt older than the 2-minute reclaim
/// window). A pre-flip stamps <c>UpdatedAt = now</c>, so the enqueued <c>RetryDeliveryJob</c>'s own
/// claim matches 0 rows and bows out with "already in progress" — the operator's "Retry now" click
/// then sits dead for a full backoff cycle (~30 min) while the UI reads 'delivering'.</para>
///
/// Mirrors the assertion shape already pinned on the sibling ops-requeue leg
/// (<see cref="OpsControllerTests"/>: <c>RequeueDelivery_FromDeadLetter_LeavesClaimableStatus…</c>).
/// The claim itself is relational-only (EF InMemory can't translate ExecuteUpdate), so the
/// end-to-end "claim SUCCEEDS and actually dispatches" proof lives on real Postgres in
/// <c>LostOrderRecoveryPostgresTests.B3_*</c>. This file pins the controller-side contract.
/// </summary>
public class OrdersControllerRetryDeliveryTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrdersController Ctrl, Mock<IOrderService> Orders,
                    Mock<IBackgroundJobClient> Jobs, Guid OrgId)
        Build(ProcuLinkDbContext db)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();
        var jobs   = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns(Guid.NewGuid().ToString());

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());

        return (ctrl, orders, jobs, orgId);
    }

    private static PurchaseOrderEntity OrderWithArtifact(Guid orgId, string status)
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id                = orderId,
            OrgId             = orgId,
            SupplierId        = Guid.NewGuid(),
            PoNumber          = "PO-RETRY",
            BuyerName         = "Buyer",
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

    [Fact]
    public async Task RetryDelivery_LeavesOrderInClaimableStatus_NotFreshDelivering()
    {
        await using var db = NewDb();
        var (ctrl, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryFailed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RetryDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();

        var persisted = await db.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == order.Id && o.OrgId == orgId);

        // NOT 'delivering' — a fresh 'delivering' is exactly what RetryDeliveryAsync's claim rejects,
        // which strands the retry for a full ~30-minute backoff cycle.
        persisted.Status.Should().NotBe(OrderStatusConstants.Delivering);
        // A claimable, send-ready idle status so the enqueued RetryDeliveryJob's claim SUCCEEDS now.
        persisted.Status.Should().BeOneOf(
            OrderStatusConstants.DeliveryFailed, OrderStatusConstants.ReadyToDeliver);
    }

    [Fact]
    public async Task RetryDelivery_EnqueuesRetryDeliveryJob()
    {
        await using var db = NewDb();
        var (ctrl, orders, jobs, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryFailed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RetryDelivery(order.Id, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        jobs.Verify(j => j.Create(
            It.Is<Job>(job => job.Type == typeof(ProcuLink.Infrastructure.Jobs.RetryDeliveryJob)),
            It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task RetryDelivery_ResponseReportsTheStatusActuallyPersisted()
    {
        // The 202 body must not claim 'delivering' while the row says otherwise — the job flips it
        // within seconds, and the honest body mirrors the ops-requeue leg's
        // `{ status = delivery_failed, requeued = true }`.
        await using var db = NewDb();
        var (ctrl, orders, _, orgId) = Build(db);

        var order = OrderWithArtifact(orgId, OrderStatusConstants.DeliveryFailed);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await ctrl.RetryDelivery(order.Id, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var persisted = await db.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == order.Id && o.OrgId == orgId);

        var reported = accepted.Value!.GetType().GetProperty("status")!.GetValue(accepted.Value) as string;
        reported.Should().Be(persisted.Status);
    }
}
