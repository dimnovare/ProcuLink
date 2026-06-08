using System.Text;
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
using Microsoft.EntityFrameworkCore;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Supplier Catalog Phase 2 — grounding the AI suggestion path in the supplier's REAL
/// product catalog. Verifies <see cref="OrderIngestionService.BuildLineEntitiesAsync"/>:
///   • when the supplier HAS catalog rows, the closest real products are retrieved
///     (dependency-free lexical match) and passed to the AI service as catalog candidates,
///     so the model can only pick a real code (the allow-list guard rejects the rest);
///   • when the supplier has NO catalog, the candidate set is exactly the past-mapping
///     candidates — byte-for-byte today's behaviour (offer ⇔ works);
///   • catalog retrieval is strictly org+supplier scoped — another org's / supplier's
///     catalog never leaks into the candidate set.
///
/// Driven through <c>CreateFromFileAsync</c> (persists via the change tracker → EF InMemory-OK),
/// the same harness <see cref="OrderServiceBatchResolveTests"/> uses.
/// </summary>
public class OrderServiceCatalogGroundingTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedSupplierAsync(ProcuLinkDbContext? existing = null)
    {
        var db         = existing ?? NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Catalog Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

    private static async Task AddCatalogAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, params (string Code, string? Name, string? Barcode)[] products)
    {
        foreach (var (code, name, barcode) in products)
        {
            db.SupplierProducts.Add(new SupplierProduct
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Code = code, Name = name, Barcode = barcode, IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
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

    // One unresolved line whose description should retrieve the resistor catalog row.
    private const string ResistorCsv =
        "itemcode,description,quantity,price\n" +
        "BUYER-RES,Resistor 220 ohm 0.25W,10,0.05\n";

    private static Mock<IItemMappingService> UnresolvedMappingsMock()
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUYER-RES"] = null, // unresolved → eligible for catalog-grounded AI
            });
        return itemMappings;
    }

    [Fact]
    public async Task CreateFromFileAsync_WithCatalog_PassesCatalogCandidates_AndPicksRealCode()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId,
            ("ES-RES-220R", "Resistor 220 ohm 0.25W", "4711000220R"),
            ("ES-CAP-100N", "Capacitor 100nF", null),
            ("ES-LED-RED",  "LED 5mm red", null));

        IReadOnlyList<AiMappingCandidate>? captured = null;
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, _, candidates, _) => captured = candidates)
            // The model selects the real catalog code (mirrors the in-service allow-list outcome).
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>
            {
                [1] = new AiMappingSuggestion("ES-RES-220R", 0.88f, "closest match",
                    "Matched catalog product ES-RES-220R — \"Resistor 220 ohm 0.25W\""),
            });

        var svc = Build(db, FileStorageMock().Object, UnresolvedMappingsMock().Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // The AI service received catalog candidates (ground truth), and the resistor row
        // — the lexical match for the line — is among them.
        Assert.NotNull(captured);
        Assert.Contains(captured!, c => c.IsCatalogProduct);
        Assert.Contains(captured!, c => c.IsCatalogProduct && c.SupplierItemCode == "ES-RES-220R");

        // The suggestion (a REAL catalog code) is carried onto the unresolved line.
        var line = await db.PurchaseOrderLines.AsNoTracking().FirstAsync(l => l.OrderId == result.Value!.Id);
        Assert.Equal("ES-RES-220R", line.AiSuggestedSupplierItemCode);
        Assert.True(line.NeedsReview);                 // suggestion is a hint, not auto-applied
        Assert.Null(line.SupplierItemCode);
        Assert.Contains("catalog product ES-RES-220R", line.AiSuggestionProvenance);
    }

    [Fact]
    public async Task CreateFromFileAsync_NoCatalog_PassesOnlyMappingCandidates_Unchanged()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // No catalog rows added.

        IReadOnlyList<AiMappingCandidate>? captured = null;
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, _, candidates, _) => captured = candidates)
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var svc = Build(db, FileStorageMock().Object, UnresolvedMappingsMock().Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // With no catalog the candidate set carries NO catalog candidates — exactly today's
        // behaviour (only past mappings, of which there are none here).
        Assert.NotNull(captured);
        Assert.DoesNotContain(captured!, c => c.IsCatalogProduct);
    }

    [Fact]
    public async Task CreateFromFileAsync_CatalogRetrieval_IsOrgAndSupplierScoped()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId, ("OURS-RES-220R", "Resistor 220 ohm 0.25W", null));

        // A DIFFERENT org's supplier with a confusingly similar product — must NEVER be retrieved.
        var otherOrgId      = Guid.NewGuid();
        var otherSupplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = otherSupplierId, OrgId = otherOrgId, Name = "Other Org Supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await AddCatalogAsync(db, otherOrgId, otherSupplierId, ("THEIRS-RES-220R", "Resistor 220 ohm 0.25W", null));

        // A second supplier within the SAME org — its catalog must also not leak in.
        var siblingSupplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = siblingSupplierId, OrgId = orgId, Name = "Sibling Supplier", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await AddCatalogAsync(db, orgId, siblingSupplierId, ("SIBLING-RES-220R", "Resistor 220 ohm 0.25W", null));

        IReadOnlyList<AiMappingCandidate>? captured = null;
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, _, candidates, _) => captured = candidates)
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var svc = Build(db, FileStorageMock().Object, UnresolvedMappingsMock().Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);

        // Only this (org, supplier)'s catalog code is a candidate.
        Assert.Contains(captured!, c => c.IsCatalogProduct && c.SupplierItemCode == "OURS-RES-220R");
        Assert.DoesNotContain(captured!, c => c.SupplierItemCode == "THEIRS-RES-220R");
        Assert.DoesNotContain(captured!, c => c.SupplierItemCode == "SIBLING-RES-220R");
    }
}
