using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The transform idempotency guard (audit batch A #1). TransformAsync atomically claims
/// the order (ready/transforming → transforming) before generating anything:
/// a duplicated Hangfire run on an ALREADY-TRANSFORMED order must return a
/// <see cref="TransformResponse.Skipped"/> no-op — re-running it would upload a duplicate
/// artifact and re-enqueue delivery, double-sending the same PO to the supplier.
/// A crashed in-flight attempt (status stuck 'transforming', no artifact) stays
/// re-claimable so the retry can self-heal it.
///
/// These tests drive the InMemory (change-tracker) branch of the guard; the
/// ExecuteUpdateAsync branch is proven on real Postgres in
/// <c>Integration/TransformIdempotencyPostgresTests</c>.
/// </summary>
public class TransformIdempotencyGuardTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService Build(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("artifact-key");

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static async Task<(Guid orgId, Guid orderId)> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, string status = "ready")
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-IDEM-1",
            BuyerName  = "Acme Buyer",
            OrderDate  = new DateOnly(2026, 6, 1),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    [Fact]
    public async Task TransformAsync_RetryAfterCompletion_SkipsAndReturnsExistingArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var svc = Build(db);

        // First run completes normally.
        var first = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error);
        Assert.False(first.Value!.Skipped);

        // Duplicated Hangfire run after completion: benign no-op pointing at the
        // EXISTING artifact — no second artifact, no status change.
        var retry = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.Error);
        Assert.True(retry.Value!.Skipped);
        Assert.Equal(first.Value.ArtifactId, retry.Value.ArtifactId);

        Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, order.Status);
    }

    [Fact]
    public async Task TransformAsync_StuckTransformingOrder_IsReclaimable_AndSelfHeals()
    {
        // A previous attempt crashed mid-transform: status stuck 'transforming', no
        // artifact. The Hangfire retry must be able to re-claim and finish the job.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, status: OrderStatusConstants.Transforming);
        var svc = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.Value!.Skipped);
        Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, order.Status);
    }

    [Fact]
    public async Task TransformAsync_NonTransformableStatus_SkipsWithoutArtifact()
    {
        // pending_review with RESOLVED lines (the invoice-classified shape): not claimable —
        // the guard skips without generating anything and without touching the status.
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, status: OrderStatusConstants.PendingReview);
        var svc = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.Skipped);
        Assert.Equal(Guid.Empty, result.Value.ArtifactId);
        Assert.Equal(0, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.PendingReview, order.Status);
    }
}
