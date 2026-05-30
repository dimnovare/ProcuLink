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

public class OrderServiceAcceptAiSuggestionsTests
{
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
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            new ProcuLink.Infrastructure.Services.Detection.SupplierSchemaMappingService(
                db, NullLogger<ProcuLink.Infrastructure.Services.Detection.SupplierSchemaMappingService>.Instance));
    }

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedOrderWithLinesAsync(
        IEnumerable<PurchaseOrderLineEntity> lines)
    {
        var db      = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Test Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        var order = new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-TEST",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = OrderStatusConstants.PendingReview,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines      = lines.ToList(),
        };

        // Set OrderId FK on each line before persisting
        foreach (var l in order.Lines) l.OrderId = orderId;

        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return (db, orgId, orderId);
    }

    // ── Test (a): lines at or above threshold are accepted, suggestions cleared,
    //             and order status recomputed to "ready" when no lines remain ──

    [Fact]
    public async Task AcceptAiSuggestionsAsync_LinesAboveThreshold_AreAccepted_AndStatusBecomesReady()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 1,
                BuyerItemCode               = "BC-001",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-111",
                AiSuggestionConfidence      = 0.92f,
                AiSuggestionReason          = "Best match",
                AiSuggestionProvenance      = "catalog",
                Confidence                  = 0.0f,
            },
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 2,
                BuyerItemCode               = "BC-002",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-222",
                AiSuggestionConfidence      = 0.95f,
                AiSuggestionReason          = "Exact match",
                AiSuggestionProvenance      = "catalog",
                Confidence                  = 0.0f,
            },
        };

        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(lines);
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(orgId, orderId, minConfidence: 0.85, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);

        var order = await db.PurchaseOrders.Include(o => o.Lines).FirstAsync(o => o.Id == orderId);

        Assert.Equal(OrderStatusConstants.Ready, order.Status);
        Assert.All(order.Lines, l =>
        {
            Assert.False(l.NeedsReview);
            Assert.NotNull(l.SupplierItemCode);
            Assert.Null(l.AiSuggestedSupplierItemCode);
            Assert.Null(l.AiSuggestionConfidence);
            Assert.Null(l.AiSuggestionReason);
            Assert.Null(l.AiSuggestionProvenance);
        });

        Assert.Equal("SUP-111", order.Lines.First(l => l.LineNumber == 1).SupplierItemCode);
        Assert.Equal("SUP-222", order.Lines.First(l => l.LineNumber == 2).SupplierItemCode);
    }

    // ── Test (b): lines below threshold are left untouched and status stays
    //             pending_review ──────────────────────────────────────────────

    [Fact]
    public async Task AcceptAiSuggestionsAsync_LinesBelowThreshold_AreUntouched_AndStatusStaysPendingReview()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 1,
                BuyerItemCode               = "BC-001",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-111",
                AiSuggestionConfidence      = 0.92f,
                Confidence                  = 0.0f,
            },
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 2,
                BuyerItemCode               = "BC-002",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-222",
                AiSuggestionConfidence      = 0.60f,   // below 0.85 threshold
                Confidence                  = 0.0f,
            },
        };

        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(lines);
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(orgId, orderId, minConfidence: 0.85, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);

        var order = await db.PurchaseOrders.Include(o => o.Lines).FirstAsync(o => o.Id == orderId);

        Assert.Equal(OrderStatusConstants.PendingReview, order.Status);

        var line1 = order.Lines.First(l => l.LineNumber == 1);
        Assert.False(line1.NeedsReview);
        Assert.Equal("SUP-111", line1.SupplierItemCode);

        var line2 = order.Lines.First(l => l.LineNumber == 2);
        Assert.True(line2.NeedsReview);
        Assert.Equal("SUP-222", line2.AiSuggestedSupplierItemCode); // unchanged
    }

    // ── Test (c): correct accepted count is returned ──────────────────────────

    [Fact]
    public async Task AcceptAiSuggestionsAsync_ReturnsCorrectAcceptedCount()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 1,
                BuyerItemCode               = "BC-001",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-A",
                AiSuggestionConfidence      = 0.90f,
                Confidence                  = 0.0f,
            },
            new PurchaseOrderLineEntity
            {
                Id                          = Guid.NewGuid(),
                LineNumber                  = 2,
                BuyerItemCode               = "BC-002",
                NeedsReview                 = true,
                AiSuggestedSupplierItemCode = "SUP-B",
                AiSuggestionConfidence      = 0.70f,  // below threshold
                Confidence                  = 0.0f,
            },
            new PurchaseOrderLineEntity
            {
                Id            = Guid.NewGuid(),
                LineNumber    = 3,
                BuyerItemCode = "BC-003",
                NeedsReview   = false,                // already resolved
                SupplierItemCode = "SUP-ALREADY",
                Confidence    = 1.0f,
            },
        };

        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(lines);
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(orgId, orderId, minConfidence: 0.85, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value); // only line 1 qualifies
    }

    // ── Test (d): order not found returns failure ─────────────────────────────

    [Fact]
    public async Task AcceptAiSuggestionsAsync_OrderNotFound_ReturnsFail()
    {
        var db  = NewDb();
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(Guid.NewGuid(), Guid.NewGuid(), 0.85, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Order not found.", result.Error);
    }
}
