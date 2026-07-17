using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// B1 (lost-order) — the Skipped path of <see cref="TransformOrderJob"/>.
///
/// <para>Original intent (audit batch A #1): a Skipped transform must not blindly re-enqueue
/// delivery, because the run that produced the artifact already enqueued it — a second enqueue
/// would double-send the PO.</para>
///
/// <para>B1 correction: that reasoning fails when the FIRST run crashed AFTER committing
/// <c>ready_to_deliver</c> + the artifact but BEFORE enqueuing delivery. The Hangfire retry's
/// transform claim then matches 0 rows (already ready_to_deliver) → Skipped → and, under the old
/// behaviour, returned WITHOUT enqueuing delivery. Nothing else re-drives <c>ready_to_deliver</c>
/// (StuckOrder = parsing/transforming, StuckDelivery = delivering, SLA needs DeliveryDueAt) → the
/// PO was stranded forever, invisibly.</para>
///
/// <para>The fix: on Skipped, RE-ENQUEUE delivery ONLY for the exact stranded signature —
/// <c>ready_to_deliver</c> + a real artifact + NO delivery attempt yet. That is safe because
/// <c>DeliverOrderJob</c>'s own atomic claim (+ per-order mutex) prevents any double-send: an
/// order already delivering/delivered, or one with a prior attempt, is never re-driven here.</para>
/// </summary>
public class TransformOrderJobSkipTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static Mock<IOrderService> OrderServiceReturning(TransformResponse response)
    {
        var mock = new Mock<IOrderService>();
        mock.Setup(s => s.TransformAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<OutputFormat>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransformResponse>.Ok(response));
        return mock;
    }

    private static Mock<IBackgroundJobClient> NewBackgroundJobClient()
    {
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns(Guid.NewGuid().ToString());
        return mock;
    }

    private static async Task<(Guid OrgId, Guid OrderId)> SeedOrderAsync(ProcuLinkDbContext db, string status)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static async Task SeedArtifactAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, Guid artifactId, DateTime? createdAt = null)
    {
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "k", CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDeliveryAttemptAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, DateTime? attemptedAt = null)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "https://supplier.example",
            Status = "failed", AttemptNumber = 1, AttemptedAt = attemptedAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ── B1: stranded ready_to_deliver + no attempt → RE-ENQUEUE (recovery) ──────

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_StrandedReadyToDeliver_NoAttempt_ReEnqueuesDeliveryOnce()
    {
        // The exact B1 signature: the first run committed ready_to_deliver + artifact then crashed
        // before enqueuing delivery. The retried job sees Skipped and MUST recover the lost delivery.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(db, orgId, orderId, artifactId);

        var jobs = NewBackgroundJobClient();
        var analytics = new FakeAnalyticsService();
        var orderService = OrderServiceReturning(
            new TransformResponse(artifactId, "csv", DateTime.UtcNow, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, analytics);

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        // Exactly one DeliverOrderJob recovered the stranded delivery.
        jobs.Verify(j => j.Create(
                It.Is<Job>(job => job.Type == typeof(DeliverOrderJob)),
                It.IsAny<IState>()),
            Times.Once,
            "a stranded ready_to_deliver order with no delivery attempt must have delivery re-enqueued");
        // Analytics only fires on a real (non-skipped) first transform, never on the recovery path.
        Assert.Empty(analytics.CapturedEvents);
    }

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_Recovery_EnqueuesWithRequireAutoDeliverTrue_NeverForceSends()
    {
        // A MANUAL order recovered by the inline B1 path must NEVER be force-sent: the recovery
        // re-drives through DeliverOrderJob.Enqueue (requireAutoDeliver=TRUE), so a manual order
        // (AutoDeliver=false) no-ops at dispatch instead of being force-dispatched. This locks that
        // the inline recovery never regresses to EnqueueRedeliver (requireAutoDeliver=false).
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(db, orgId, orderId, artifactId);

        Job? captured = null;
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((j, _) => captured = j)
            .Returns(Guid.NewGuid().ToString());

        var orderService = OrderServiceReturning(
            new TransformResponse(artifactId, "csv", DateTime.UtcNow, Skipped: true));
        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, new FakeAnalyticsService());

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(typeof(DeliverOrderJob), captured!.Type);
        // DeliverOrderJob.ExecuteAsync(orderId, org, artifactId, requireAutoDeliver, ct) — arg[3] must be TRUE.
        Assert.True((bool)captured.Args[3]!);
    }

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_AlreadyHasDeliveryAttempt_DoesNotReEnqueue()
    {
        // Double-send guard preserved: an order that already had a delivery attempt (delivery ran /
        // is running) must NOT be re-enqueued even though the transform Skipped.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(db, orgId, orderId, artifactId);
        await SeedDeliveryAttemptAsync(db, orgId, orderId);

        var jobs = NewBackgroundJobClient();
        var orderService = OrderServiceReturning(
            new TransformResponse(artifactId, "csv", DateTime.UtcNow, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, new FakeAnalyticsService());

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never,
            "an order with a prior delivery attempt must not be re-enqueued (double-send guard)");
    }

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_StaleAttemptOlderThanCurrentArtifact_ReEnqueuesDelivery()
    {
        // An attempt row is not evidence that THIS artifact was dispatched. Here the order was
        // delivered/failed against an OLD artifact, the operator edited the mapping (which resets a
        // past-ready order to Ready), and the re-transform committed a NEW artifact + ready_to_deliver
        // before crashing in the enqueue gap. Nothing deletes the old attempt row on re-transform, so
        // the Hangfire retry's Skipped path must still recover this strand.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var now = DateTime.UtcNow;

        await SeedArtifactAsync(db, orgId, orderId, Guid.NewGuid(), now.AddMinutes(-45));
        await SeedDeliveryAttemptAsync(db, orgId, orderId, now.AddMinutes(-40));
        var currentArtifactId = Guid.NewGuid();
        await SeedArtifactAsync(db, orgId, orderId, currentArtifactId, now.AddMinutes(-35));

        var jobs = NewBackgroundJobClient();
        var orderService = OrderServiceReturning(
            new TransformResponse(currentArtifactId, "csv", now, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, new FakeAnalyticsService());

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(
                It.Is<Job>(x => x.Type == typeof(DeliverOrderJob)),
                It.IsAny<IState>()),
            Times.Once,
            "an attempt predating the current artifact never dispatched it — the strand must still be recovered");
    }

    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    public async Task ExecuteAsync_SkippedTransform_NonReadyStatus_DoesNotReEnqueue(string status)
    {
        // Only the ready_to_deliver strand is recoverable here. A concurrent in-flight delivery or a
        // terminal state must never be re-driven from the transform Skipped path.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, status);
        var artifactId = Guid.NewGuid();
        await SeedArtifactAsync(db, orgId, orderId, artifactId);

        var jobs = NewBackgroundJobClient();
        var orderService = OrderServiceReturning(
            new TransformResponse(artifactId, "csv", DateTime.UtcNow, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, new FakeAnalyticsService());

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never,
            "a non-ready_to_deliver order must not be re-enqueued from the transform Skipped path");
    }

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_NoArtifact_DoesNotReEnqueue()
    {
        // Skipped with an empty artifact id (no artifact exists) — there is nothing to deliver.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);

        var jobs = NewBackgroundJobClient();
        var orderService = OrderServiceReturning(
            new TransformResponse(Guid.Empty, "csv", DateTime.UtcNow, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, new FakeAnalyticsService());

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never,
            "no artifact means nothing to deliver — no re-enqueue");
    }

    // ── Real (non-skipped) transform still enqueues exactly one delivery ─────────

    [Fact]
    public async Task ExecuteAsync_RealTransform_StillEnqueuesDelivery()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var jobs = NewBackgroundJobClient();
        var analytics = new FakeAnalyticsService();
        var orderService = OrderServiceReturning(
            new TransformResponse(Guid.NewGuid(), "csv", DateTime.UtcNow));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, analytics);

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once,
            "a real (non-skipped) transform hands off to DeliverOrderJob exactly once");
    }
}
