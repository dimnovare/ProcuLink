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
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// OrderService.MarkRejectedAsync — manual out-of-band rejection capture.
/// </summary>
public class OrderServiceMarkRejectedTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
    {
        var parserFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
        });

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AiMappingLineContext>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiMappingSuggestion?)null);

        var fileStorage = new Mock<IFileStorageService>();

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage.Object,
            parserFactory,
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedOrderAsync(
        string status = OrderStatusConstants.DeliveryFailed)
    {
        var db      = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now     = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-MR-001",
            OrderDate  = DateOnly.FromDateTime(now),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = now,
            UpdatedAt  = now,
        });

        await db.SaveChangesAsync();
        return (db, orgId, orderId);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkRejectedAsync_SetsStatusAndWritesAuditEvent()
    {
        var (db, orgId, orderId) = await SeedOrderAsync();
        var svc = BuildService(db);

        var result = await svc.MarkRejectedAsync(
            orgId, orderId, "Wrong item codes in payload", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatusConstants.RejectedBySupplier, result.Value!.Status);

        var order = await db.PurchaseOrders
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.RejectedBySupplier, order.Status);

        var auditEvent = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == orderId && e.Action == "MarkedRejected")
            .FirstOrDefaultAsync();
        Assert.NotNull(auditEvent);

        var reasonProp = auditEvent!.Payload!.RootElement.GetProperty("reason").GetString();
        Assert.Equal("Wrong item codes in payload", reasonProp);
    }

    [Fact]
    public async Task MarkRejectedAsync_ClosesSlaWindow()
    {
        var (db, orgId, orderId) = await SeedOrderAsync(OrderStatusConstants.Delivering);

        // Order was mid-delivery with a live, overdue SLA window the sweep had already flagged.
        var seeded = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        seeded.DeliveryDueAt = DateTime.UtcNow.AddMinutes(-5);
        seeded.SlaBreached   = true;
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var result = await svc.MarkRejectedAsync(
            orgId, orderId, "Wrong item codes in payload", CancellationToken.None);

        Assert.True(result.IsSuccess);

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        // A manual terminal rejection settles the order — it must not keep nagging "delivery overdue".
        Assert.Null(order.DeliveryDueAt);
        Assert.False(order.SlaBreached);
    }

    [Fact]
    public async Task MarkRejectedAsync_WithExistingDeliveryAttempt_WritesRejectionReasonOnAttempt()
    {
        var (db, orgId, orderId) = await SeedOrderAsync();

        // Seed an existing delivery attempt for this order
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id          = Guid.NewGuid(),
            OrderId     = orderId,
            OrgId       = orgId,
            Channel     = "http",
            Destination = "https://supplier.example/orders",
            Status      = "failed",
            AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow,
            ResponseCode = 422,
            ErrorMessage = "422 Unprocessable Entity",
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var result = await svc.MarkRejectedAsync(
            orgId, orderId, "Buyer item code not in supplier catalog", CancellationToken.None);

        Assert.True(result.IsSuccess);

        var attempt = await db.DeliveryAttempts
            .AsNoTracking()
            .SingleAsync(a => a.OrderId == orderId);
        Assert.Equal("Buyer item code not in supplier catalog", attempt.RejectionReason);
    }

    [Fact]
    public async Task MarkRejectedAsync_OrderNotFound_ReturnsFailure()
    {
        var (db, orgId, _) = await SeedOrderAsync();
        var svc = BuildService(db);

        var result = await svc.MarkRejectedAsync(
            orgId, Guid.NewGuid(), "some reason", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }

    [Fact]
    public async Task MarkRejectedAsync_WrongOrg_ReturnsFailure()
    {
        var (db, _, orderId) = await SeedOrderAsync();
        var svc = BuildService(db);

        var result = await svc.MarkRejectedAsync(
            Guid.NewGuid(), orderId, "some reason", CancellationToken.None);

        Assert.False(result.IsSuccess);
    }
}
