using System.Text.Json;
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
/// Tier-D #3 — first_upload_parsed is a once-per-ORDER milestone, not a per-parse event.
///
/// The org-level guard ("does any OTHER order for this org sit in a parsed state") does not stop a
/// re-parse of an org's ONLY order from firing it twice: routing's assign-supplier flips an
/// 'unrouted' order back to 'parsing' and re-parses it, and the AnyAsync still finds no other
/// parsed order. ParseStoredFileAsync writes exactly one 'Parsed' audit event per parse, so more
/// than one for this order means re-parse.
/// </summary>
public class ParseOrderJobReParseAnalyticsTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    /// <summary>Seeds the org's single order plus <paramref name="parseCount"/> 'Parsed' audit events.</summary>
    private static async Task SeedOrderWithParseHistoryAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, int parseCount)
    {
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

        for (var i = 0; i < parseCount; i++)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                UserId     = null,
                EntityType = "Order",
                EntityId   = orderId,
                Action     = "Parsed",
                Payload    = JsonDocument.Parse("""{"lineCount":1}"""),
                CreatedAt  = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        await db.SaveChangesAsync();
    }

    private static ParseOrderJob NewJob(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, FakeAnalyticsService analytics)
    {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(
                  new PurchaseOrderEntity
                  {
                      Id = orderId, OrgId = orgId, Status = OrderStatusConstants.PendingReview,
                  },
                  null,
                  "csv")));

        return new ParseOrderJob(
            orders.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics,
            new Mock<ProcuLink.Core.Services.Detection.ISchemaFingerprintService>().Object);
    }

    [Fact]
    public async Task ExecuteAsync_ReParseOfOrgsOnlyOrder_DoesNotReEmit()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        // Two 'Parsed' events = the first parse plus this re-parse.
        await SeedOrderWithParseHistoryAsync(db, orgId, orderId, parseCount: 2);

        var analytics = new FakeAnalyticsService();
        await NewJob(db, orgId, orderId, analytics).ExecuteAsync(orderId, orgId, CancellationToken.None);

        analytics.CapturedEvents.Should().BeEmpty(
            "first_upload_parsed already fired on this order's first parse — a re-parse must not double-count it");
    }

    [Fact]
    public async Task ExecuteAsync_GenuineFirstParse_StillEmits()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        // Exactly one 'Parsed' event — the shape a real first parse leaves behind.
        await SeedOrderWithParseHistoryAsync(db, orgId, orderId, parseCount: 1);

        var analytics = new FakeAnalyticsService();
        await NewJob(db, orgId, orderId, analytics).ExecuteAsync(orderId, orgId, CancellationToken.None);

        analytics.CapturedEvents.Should().ContainSingle()
            .Which.EventName.Should().Be("first_upload_parsed");
    }
}
