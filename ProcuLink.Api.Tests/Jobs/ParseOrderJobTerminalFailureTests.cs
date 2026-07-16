using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Tier-D #2 — a TERMINAL parse failure must not be reported to Hangfire as a success.
///
/// The first attempt fails: the service sets status='failed' and returns Fail, and the job throws.
/// Hangfire then RETRIES, and the retry re-enters ParseStoredFileAsync, whose status!='parsing'
/// re-entry guard treats the now-'failed' order as an already-processed SKIP and returns Ok. The
/// job used to log success there, so the whole job landed Succeeded and every terminal parse
/// failure was invisible in the Failed queue. It must throw instead.
/// </summary>
public class ParseOrderJobTerminalFailureTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static ParseOrderJob NewJob(
        ProcuLinkDbContext db, IOrderService orders, FakeAnalyticsService analytics) =>
        new(orders,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics,
            new Mock<ProcuLink.Core.Services.Detection.ISchemaFingerprintService>().Object);

    private static Mock<IOrderService> OrderServiceReturning(Guid orgId, Guid orderId, string status)
    {
        var mock = new Mock<IOrderService>();
        mock.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(
                new PurchaseOrderEntity { Id = orderId, OrgId = orgId, Status = status },
                null,
                "unknown")));
        return mock;
    }

    [Fact]
    public async Task ExecuteAsync_RetrySeesTerminallyFailedOrder_Throws()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Failed).Object, analytics);

        var act = async () => await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a terminally failed parse must land in Hangfire's Failed queue, not report Succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_TerminallyFailedOrder_DoesNotEmitAnalytics()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Failed).Object, analytics);

        try { await job.ExecuteAsync(orderId, orgId, CancellationToken.None); }
        catch (InvalidOperationException) { /* expected — asserted in the sibling test */ }

        analytics.CapturedEvents.Should().BeEmpty(
            "first_upload_parsed must never fire for an order whose parse terminally failed");
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalSkip_StillSucceeds()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Ready).Object, analytics);

        var act = async () => await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a legitimate already-processed skip (ready / pending_review / unrouted) is not a failure");
    }
}
