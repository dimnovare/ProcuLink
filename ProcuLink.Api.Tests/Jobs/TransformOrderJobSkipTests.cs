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
/// Audit batch A #1 (job half): when TransformAsync reports a
/// <see cref="TransformResponse.Skipped"/> no-op (the order was already transformed or is
/// in flight), the job must NOT enqueue <c>DeliverOrderJob</c> — the run that produced the
/// artifact already did, and a second enqueue dispatches the same PO to the supplier twice.
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

    [Fact]
    public async Task ExecuteAsync_SkippedTransform_DoesNotEnqueueDelivery_OrEmitAnalytics()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OrderStatusConstants.ReadyToDeliver);
        var jobs = NewBackgroundJobClient();
        var analytics = new FakeAnalyticsService();
        var orderService = OrderServiceReturning(
            new TransformResponse(Guid.NewGuid(), "csv", DateTime.UtcNow, Skipped: true));

        var job = new TransformOrderJob(
            orderService.Object, jobs.Object, NullLogger<TransformOrderJob>.Instance, db, analytics);

        await job.ExecuteAsync(orderId, orgId, "csv", CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never,
            "a skipped transform must never re-enqueue delivery — that double-sends the PO");
        Assert.Empty(analytics.CapturedEvents);
    }

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
