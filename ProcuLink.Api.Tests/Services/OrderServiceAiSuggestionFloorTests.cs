using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// AI / fuzzy-catalog suggestion confidence floor (bug: a 0.60 "ACME-JSON-2" suggestion for a
/// Yubico YubiKey was surfaced as a line hint and misled the reviewer). Verifies
/// <see cref="OrderIngestionService.BuildLineEntitiesAsync"/> drops an AI suggestion whose
/// confidence is below the 0.65 floor (AiSuggested* left null → "no confident match — enter
/// manually"), and keeps one at or above the floor.
///
/// This applies ONLY to the AI/fuzzy-catalog path (the dictionary returned by
/// <see cref="IAiMappingService.SuggestSupplierItemCodesAsync"/>); the separate
/// source-manufacturer-part-number suggestion (a real code stated in the document, emitted at
/// 0.95) is unaffected — there is no MPN in these fixtures.
///
/// Mirrors the harness in <see cref="OrderServiceCatalogGroundingTests"/>.
/// </summary>
public class OrderServiceAiSuggestionFloorTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedSupplierAsync()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Floor Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

    private static Mock<IFileStorageService> FileStorageMock()
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploaded-key");
        return fileStorage;
    }

    // One unresolved line — eligible for an AI suggestion, no manufacturer part number present.
    private const string YubiKeyCsv =
        "itemcode,description,quantity,price\n" +
        "BUYER-YK5,Yubico YubiKey 5 NFC,3,55.00\n";

    private static Mock<IItemMappingService> UnresolvedMappingsMock()
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUYER-YK5"] = null, // unresolved → eligible for an AI suggestion
            });
        return itemMappings;
    }

    private static Mock<IAiMappingService> AiSuggestsMock(float confidence)
    {
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>
            {
                [1] = new AiMappingSuggestion("ACME-JSON-2", confidence, "fuzzy catalog match",
                    "Closest catalog product by description"),
            });
        return aiMappings;
    }

    private static OrderService Build(
        ProcuLinkDbContext db,
        IFileStorageService fileStorage,
        IItemMappingService itemMappings,
        IAiMappingService aiMappings)
    {
        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            itemMappings,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    [Fact]
    public async Task CreateFromFileAsync_AiSuggestionBelowFloor_IsDropped()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        // 0.60 < 0.65 floor → the suggestion must be dropped.
        var svc = Build(db, FileStorageMock().Object, UnresolvedMappingsMock().Object, AiSuggestsMock(0.60f).Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(YubiKeyCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        var line = await db.PurchaseOrderLines.AsNoTracking().FirstAsync(l => l.OrderId == result.Value!.Id);

        // Dropped: no misleading low-confidence code surfaced — the reviewer enters manually.
        Assert.Null(line.AiSuggestedSupplierItemCode);
        Assert.Null(line.AiSuggestionConfidence);
        Assert.Null(line.AiSuggestionReason);
        Assert.Null(line.AiSuggestionProvenance);
        Assert.True(line.NeedsReview);
        Assert.Null(line.SupplierItemCode);
    }

    [Fact]
    public async Task CreateFromFileAsync_AiSuggestionAtOrAboveFloor_IsKept()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        // 0.70 >= 0.65 floor → the suggestion is kept.
        var svc = Build(db, FileStorageMock().Object, UnresolvedMappingsMock().Object, AiSuggestsMock(0.70f).Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(YubiKeyCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        var line = await db.PurchaseOrderLines.AsNoTracking().FirstAsync(l => l.OrderId == result.Value!.Id);

        // Kept: the suggestion is carried onto the line as a (review-only) hint.
        Assert.Equal("ACME-JSON-2", line.AiSuggestedSupplierItemCode);
        Assert.Equal(0.70f, line.AiSuggestionConfidence);
        Assert.True(line.NeedsReview);              // still a hint, never auto-applied
        Assert.Null(line.SupplierItemCode);
    }
}
