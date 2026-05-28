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

public class TransformOrderJobEmitsFirstTransformSucceededTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static Mock<IOrderService> NewOrderServiceReturningSuccess(Guid artifactId)
    {
        var mock = new Mock<IOrderService>();
        mock.Setup(s => s.TransformAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<OutputFormat>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransformResponse>.Ok(
                new TransformResponse(artifactId, "xml", DateTime.UtcNow)));
        return mock;
    }

    private static Mock<IBackgroundJobClient> NewBackgroundJobClient()
    {
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns(Guid.NewGuid().ToString());
        return mock;
    }

    [Fact]
    public async Task ExecuteAsync_FirstTransformForOrg_EmitsFirstTransformSucceeded()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            Status = OrderStatusConstants.Transforming,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orderService = NewOrderServiceReturningSuccess(artifactId);
        var jobs = NewBackgroundJobClient();
        var analytics = new FakeAnalyticsService();

        var job = new TransformOrderJob(
            orderService.Object,
            jobs.Object,
            NullLogger<TransformOrderJob>.Instance,
            db,
            analytics);

        await job.ExecuteAsync(orderId, orgId, "xml", CancellationToken.None);

        Assert.Single(analytics.CapturedEvents);
        var ev = analytics.CapturedEvents[0];
        Assert.Equal("first_transform_succeeded", ev.EventName);
        Assert.Equal(orgId, ev.OrgId);
        Assert.Null(ev.UserId);
        Assert.Equal(orderId, ev.Properties["order_id"]);
        Assert.Equal("xml", ev.Properties["output_format"]);
    }

    [Fact]
    public async Task ExecuteAsync_OrgAlreadyHasTransformedOrders_DoesNotEmit()
    {
        var orgId = Guid.NewGuid();
        var currentOrderId = Guid.NewGuid();
        var priorOrderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = priorOrderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            Status = OrderStatusConstants.Delivered,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = currentOrderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orderService = NewOrderServiceReturningSuccess(artifactId);
        var jobs = NewBackgroundJobClient();
        var analytics = new FakeAnalyticsService();

        var job = new TransformOrderJob(
            orderService.Object,
            jobs.Object,
            NullLogger<TransformOrderJob>.Instance,
            db,
            analytics);

        await job.ExecuteAsync(currentOrderId, orgId, "csv", CancellationToken.None);

        Assert.Empty(analytics.CapturedEvents);
    }
}
