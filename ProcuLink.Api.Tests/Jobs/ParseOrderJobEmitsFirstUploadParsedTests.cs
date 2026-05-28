using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Api.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

public class ParseOrderJobEmitsFirstUploadParsedTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ExecuteAsync_FirstParseForOrg_EmitsFirstUploadParsed()
    {
        var orgId    = Guid.NewGuid();
        var orderId  = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-1",
            Currency      = "EUR",
            Status        = OrderStatusConstants.PendingReview,
            SourceFileKey = "uploads/some-file.csv",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orderServiceMock = new Mock<IOrderService>();
        orderServiceMock
            .Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, Status = OrderStatusConstants.PendingReview,
            }));

        var analytics = new FakeAnalyticsService();
        var job = new ParseOrderJob(
            orderServiceMock.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics);

        await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        Assert.Single(analytics.CapturedEvents);
        var ev = analytics.CapturedEvents[0];
        Assert.Equal("first_upload_parsed", ev.EventName);
        Assert.Equal(orgId, ev.OrgId);
        Assert.Null(ev.UserId);
        Assert.Equal(orderId, ev.Properties["order_id"]);
        var parser = Assert.IsType<string>(ev.Properties["parser"]);
        Assert.Contains(parser, new[] { "csv", "xlsx", "pdf", "unknown" });
        Assert.Equal("csv", parser);
    }

    [Fact]
    public async Task ExecuteAsync_OrgAlreadyHasParsedOrders_DoesNotEmit()
    {
        var orgId          = Guid.NewGuid();
        var priorOrderId   = Guid.NewGuid();
        var currentOrderId = Guid.NewGuid();

        await using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = priorOrderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-PRIOR",
            Currency      = "EUR",
            Status        = OrderStatusConstants.Ready,
            SourceFileKey = "uploads/prior.csv",
            CreatedAt     = DateTime.UtcNow.AddDays(-1),
            UpdatedAt     = DateTime.UtcNow.AddDays(-1),
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = currentOrderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-CURR",
            Currency      = "EUR",
            Status        = OrderStatusConstants.PendingReview,
            SourceFileKey = "uploads/current.xlsx",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orderServiceMock = new Mock<IOrderService>();
        orderServiceMock
            .Setup(s => s.ParseStoredFileAsync(orgId, currentOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = currentOrderId, OrgId = orgId, Status = OrderStatusConstants.PendingReview,
            }));

        var analytics = new FakeAnalyticsService();
        var job = new ParseOrderJob(
            orderServiceMock.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics);

        await job.ExecuteAsync(currentOrderId, orgId, CancellationToken.None);

        Assert.Empty(analytics.CapturedEvents);
    }
}
