using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Tokenizing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-17 — the gate must also stand on the DELIVERY side, because that is where the mainline
/// manual send actually goes.
///
/// <para><b>The hole this closes.</b> Gating only the transform door was not enough.
/// <c>Organisation.AutoDeliver</c> defaults to FALSE, so <c>ready_to_deliver</c> is the normal
/// resting state of a live order and the inbox's "Send selected" — the mainline manual send — does
/// NOT call <c>POST /api/orders/{id}/transform</c>. It calls <c>POST /api/orders/{id}/redeliver</c>
/// once per selected row, which enqueued <c>DeliverOrderJob</c> against a STORED artifact without
/// ever consulting the gate. Every status the inbox lets you select
/// (<c>ready_to_deliver</c>, <c>delivery_failed</c>, <c>delivery_unconfirmed</c>) already holds such
/// an artifact, so the supplier's blocking rules were bypassed on the most-used send path in the
/// product.</para>
///
/// <para><b>Which paths are gated, and which deliberately are not.</b> The line is: the gate
/// answers "may a person send this order?" at every point where a HUMAN decides to send, and it
/// does NOT re-answer inside an automatic continuation of a send it already admitted.
/// <list type="bullet">
///   <item><description>GATED — <c>OrdersController.Redeliver</c> (inbox bulk-send + the parked
///     order's "Send again"), <c>OrdersController.RetryDelivery</c> ("Retry now"),
///     <c>OpsController.RequeueDelivery</c> (the ops escalation). Each is a fresh human decision to
///     put this document in front of the supplier, taken possibly long after the transform, and the
///     supplier's rules may have changed in between.</description></item>
///   <item><description>NOT GATED — <c>RetryDeliveryJob</c>'s backoff queue and
///     <c>DeliverOrderJob</c>'s scheduled retry. These continue a dispatch that ALREADY started and
///     failed transiently; delivery is at-least-once, so the supplier may already hold the document.
///     Refusing mid-chain un-sends nothing and converts a transient failure into a permanent
///     strand.</description></item>
///   <item><description>NOT GATED — <c>StrandedReadyOrderDetectionService</c> and
///     <c>TransformOrderJob</c>'s inline stranded recovery. Both complete a delivery whose enqueue
///     was LOST between the transform commit and <c>DeliverOrderJob.Enqueue</c>; the artifact they
///     re-drive was produced by a transform this gate had already admitted, seconds or minutes
///     earlier. They finish an authorised send, they do not start one.</description></item>
/// </list></para>
///
/// <para>Every refusal here is paired with a NEGATIVE CONTROL that changes ONE field — the currency
/// the supplier's rule judges — and asserts the send goes through. Without it a green test proves
/// only that something refused the order.</para>
/// </summary>
public sealed class AcceptanceGateBlocksDeliveryTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── POST /api/orders/{id}/redeliver — the inbox bulk-send ─────────────────

    [Fact]
    public async Task Redeliver_ofAnOrderTheSupplierRefuses_isRejected_andEnqueuesNothing()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", status: OrderStatusConstants.ReadyToDeliver);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        var result = await ctrl.Redeliver(seed.OrderId, CancellationToken.None);

        AssertRefused(result);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    /// <summary>
    /// NEGATIVE CONTROL — identical fixture, identical endpoint, identical rule. The ONE difference
    /// is the currency the rule judges. If this were also refused, the refusal above would not be
    /// about the gate at all.
    /// </summary>
    [Fact]
    public async Task Redeliver_whenTheOrderSatisfiesTheRule_isAccepted_andEnqueuesTheDelivery()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "EUR", status: OrderStatusConstants.ReadyToDeliver);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        var result = await ctrl.Redeliver(seed.OrderId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, StatusOf(result));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    /// <summary>
    /// The parked order's "Send again" runs the SAME endpoint from <c>delivery_unconfirmed</c>, and
    /// the inbox lets you bulk-select that status. It must be refused too — a parked order that the
    /// supplier's rules now refuse is exactly the case where a second send is worst.
    /// </summary>
    [Fact]
    public async Task Redeliver_fromDeliveryUnconfirmed_isAlsoRejected()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", status: OrderStatusConstants.DeliveryUnconfirmed);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        AssertRefused(await ctrl.Redeliver(seed.OrderId, CancellationToken.None));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    /// <summary>An operator override is the way out on the delivery side too, exactly as on the
    /// transform side — otherwise "record an override and send it anyway" would be advice the
    /// product cannot honour on the path the operator is actually standing on.</summary>
    [Fact]
    public async Task Redeliver_withARecordedOverride_goesThrough()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", status: OrderStatusConstants.ReadyToDeliver);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        AssertRefused(await ctrl.Redeliver(seed.OrderId, CancellationToken.None));

        var recorded = await new AcceptanceGate(db, new SupplierAcceptanceService(db))
            .RecordOverrideAsync(seed.OrgId, seed.OrderId, "user_2opsLead",
                "Supplier confirmed USD for this PO (TCK-4412).", CancellationToken.None);
        Assert.True(recorded.Recorded, recorded.Error);

        Assert.Equal(StatusCodes.Status202Accepted,
            StatusOf(await ctrl.Redeliver(seed.OrderId, CancellationToken.None)));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    // ── POST /api/orders/{id}/retry-delivery — "Retry now" ────────────────────

    [Fact]
    public async Task RetryDelivery_ofAnOrderTheSupplierRefuses_isRejected_andEnqueuesNothing()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", status: OrderStatusConstants.DeliveryFailed);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        AssertRefused(await ctrl.RetryDelivery(seed.OrderId, CancellationToken.None));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task RetryDelivery_whenTheOrderSatisfiesTheRule_isAccepted()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "EUR", status: OrderStatusConstants.DeliveryFailed);
        var (ctrl, jobs) = BuildOrdersController(db, seed);

        Assert.Equal(StatusCodes.Status202Accepted,
            StatusOf(await ctrl.RetryDelivery(seed.OrderId, CancellationToken.None)));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    // ── POST /api/ops/orders/{id}/requeue-delivery — the ops escalation ───────

    /// <summary>
    /// The ops requeue must be refused BEFORE it mutates anything: it supersedes the attempt cap and
    /// rewrites the order's status in one SaveChanges. A refusal that arrived after that write would
    /// leave the order's cap reset and its status rewritten for a send that never happens.
    /// </summary>
    [Fact]
    public async Task OpsRequeue_ofAnOrderTheSupplierRefuses_isRejected_andChangesNothing()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "USD", status: OrderStatusConstants.DeliveryDeadLetter);
        var (ctrl, jobs) = BuildOpsController(db, seed);

        AssertRefused(await ctrl.RequeueDelivery(seed.OrderId, CancellationToken.None));

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);

        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == seed.OrderId).Select(o => o.Status).FirstAsync();
        Assert.Equal(OrderStatusConstants.DeliveryDeadLetter, status);

        Assert.Empty(await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == seed.OrderId && a.Action == "DeliveryRequeuedByOperator")
            .ToListAsync());
    }

    [Fact]
    public async Task OpsRequeue_whenTheOrderSatisfiesTheRule_isAccepted()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, currency: "EUR", status: OrderStatusConstants.DeliveryDeadLetter);
        var (ctrl, jobs) = BuildOpsController(db, seed);

        Assert.Equal(StatusCodes.Status202Accepted,
            StatusOf(await ctrl.RequeueDelivery(seed.OrderId, CancellationToken.None)));
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid OrgId, Guid OrderId, PurchaseOrderEntity Order);

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o   => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _                => StatusCodes.Status200OK,
    };

    /// <summary>
    /// The refusal an operator sees: a 409 whose body carries the SAME plain sentence the transform
    /// refusal carries — what failed, actual vs expected, the fix, and the two ways out.
    /// </summary>
    private static void AssertRefused(IActionResult result)
    {
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));

        var body = Assert.IsType<ConflictObjectResult>(result).Value!;
        var error = body.GetType().GetProperty("error")?.GetValue(body) as string;

        Assert.NotNull(error);
        Assert.Contains("Currency must be EUR", error!);
        Assert.Contains("USD", error!);
        Assert.Contains("Set currency to EUR", error!);
        Assert.DoesNotContain("failed rule", error!);
        Assert.DoesNotContain("BlockOnFail", error!);
    }

    private static (OrdersController Ctrl, Mock<IBackgroundJobClient> Jobs) BuildOrdersController(
        ProcuLinkDbContext db, Seeded seed)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(seed.OrgId);

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(seed.OrgId, seed.OrderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(seed.Order));

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(seed.OrgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: "operations", Limit: 1000));

        var jobs = new Mock<IBackgroundJobClient>();

        // The REAL acceptance service against the REAL DbContext — the controller builds its gate
        // from these two when DI supplies none, so this exercises the production wiring.
        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            db,
            NullLogger<OrdersController>.Instance,
            billing.Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new SupplierAcceptanceService(db),
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());

        return (ctrl, jobs);
    }

    private static (OpsController Ctrl, Mock<IBackgroundJobClient> Jobs) BuildOpsController(
        ProcuLinkDbContext db, Seeded seed)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(seed.OrgId);

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(seed.OrgId, seed.OrderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(seed.Order));

        var jobs = new Mock<IBackgroundJobClient>();

        var ctrl = new OpsController(
            new Mock<IOpsHealthService>().Object,
            tenant.Object,
            orders.Object,
            jobs.Object,
            db,
            NullLogger<OpsController>.Instance);

        return (ctrl, jobs);
    }

    /// <summary>
    /// A fully-resolved order that has ALREADY been transformed — it holds a stored artifact and
    /// sits in a delivery status, which is the shape every gated endpoint here operates on. Its
    /// supplier has one active error-severity rule judging the currency, so nothing but that rule
    /// can be the thing that refuses it.
    /// </summary>
    private static async Task<Seeded> SeedAsync(ProcuLinkDbContext db, string currency, string status)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Gate Org", Slug = $"gate-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });

        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-DELIVER-1", BuyerName = "Buyer Ltd", Currency = currency,
            OrderDate = new DateOnly(2026, 7, 30), Status = status,
            CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
            OutboundArtifacts =
            {
                new OutboundArtifact
                {
                    Id = artifactId, OrderId = orderId, OrgId = orgId,
                    Format = "csv", FileKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv",
                    CreatedAt = now,
                },
            },
        };
        db.PurchaseOrders.Add(order);

        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "active", CreatedAt = now, EffectiveFrom = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                    ExpectedValue = "EUR", Severity = "error", BlockOnFail = false,
                },
            },
        });

        await db.SaveChangesAsync();

        // The controllers read the order through IOrderService.GetByIdAsync (mocked above) — hand
        // them a DETACHED copy carrying the artifact, exactly as the real AsNoTracking read does.
        return new Seeded(orgId, orderId, order);
    }
}
