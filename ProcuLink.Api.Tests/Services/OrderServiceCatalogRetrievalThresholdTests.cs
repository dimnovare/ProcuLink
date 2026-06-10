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
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V10 — indexed catalog retrieval at scale. Verifies that
/// <see cref="OrderIngestionService.BuildCatalogGroundedCandidatesAsync"/> uses the INDEXED path
/// only above the threshold, that exact matches lead the candidate set, and that it falls back to
/// the byte-identical in-memory path when the indexed path is unavailable (non-Postgres / fallback).
///
/// A <see cref="FakeCatalogRetrievalService"/> stands in for the real (Postgres-only) service so
/// these can run on the EF InMemory provider; the real trigram ranking is verified live on Postgres.
/// </summary>
public class OrderServiceCatalogRetrievalThresholdTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedSupplierAsync()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Scale Supplier", CreatedAt = DateTime.UtcNow });
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

    private const string ResistorCsv =
        "itemcode,description,quantity,price\n" +
        "BUYER-RES,Resistor 220 ohm 0.25W,10,0.05\n";

    private static Mock<IFileStorageService> FileStorageMock()
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploaded-key");
        return fileStorage;
    }

    private static Mock<IItemMappingService> UnresolvedMappingsMock()
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["BUYER-RES"] = null });
        return itemMappings;
    }

    private static (OrderService svc, Func<IReadOnlyList<AiMappingCandidate>?> captured) Build(
        ProcuLinkDbContext db, IAiMappingService aiMappings, ICatalogRetrievalService retrieval)
    {
        var poMappings = new Mock<IPoMappingService>();
        poMappings.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger.Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new OrderService(
            db,
            FileStorageMock().Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            UnresolvedMappingsMock().Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            structuredExtractor: null,
            aiDecisions: null,
            catalogRetrieval: retrieval);

        return (svc, () => null);
    }

    private static (Mock<IAiMappingService> mock, Func<IReadOnlyList<AiMappingCandidate>?> captured) CaptureAiMock()
    {
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
        return (aiMappings, () => captured);
    }

    [Fact]
    public async Task AboveThreshold_UsesIndexedRetrieval_ProductsBecomeCatalogCandidates()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // The DB catalog deliberately contains a DIFFERENT code than what the indexed service
        // returns — proving the candidates came from the indexed service, not the in-memory load.
        await AddCatalogAsync(db, orgId, supplierId, ("IN-MEMORY-ONLY", "Should not appear", null));

        var fake = new FakeCatalogRetrievalService
        {
            UseIndexed = true,
            Retrieved = new List<SupplierProduct>
            {
                new() { Code = "ES-RES-220R", Name = "Resistor 220 ohm 0.25W" },
                new() { Code = "ES-RES-330R", Name = "Resistor 330 ohm" },
            },
        };

        var (aiMock, captured) = CaptureAiMock();
        var (svc, _) = Build(db, aiMock.Object, fake);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(fake.ShouldUseCalled);
        Assert.True(fake.RetrieveCalled);

        var c = captured();
        Assert.NotNull(c);
        // Indexed products are present as catalog candidates; the in-memory-only DB code is NOT.
        Assert.Contains(c!, x => x.IsCatalogProduct && x.SupplierItemCode == "ES-RES-220R");
        Assert.Contains(c!, x => x.IsCatalogProduct && x.SupplierItemCode == "ES-RES-330R");
        Assert.DoesNotContain(c!, x => x.SupplierItemCode == "IN-MEMORY-ONLY");
    }

    [Fact]
    public async Task AboveThreshold_ExactMatchLeadsCandidateSet()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        // The indexed service returns exact-first ordering (exact code ahead of fuzzy), which the
        // candidate builder preserves: the first catalog candidate is the exact match.
        var fake = new FakeCatalogRetrievalService
        {
            UseIndexed = true,
            Retrieved = new List<SupplierProduct>
            {
                new() { Code = "BUYER-RES", Name = "Exact code match" },   // exact (pass 1)
                new() { Code = "ES-RES-FUZZY", Name = "Resistor fuzzy" },  // fuzzy (pass 2)
            },
        };

        var (aiMock, captured) = CaptureAiMock();
        var (svc, _) = Build(db, aiMock.Object, fake);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var c = captured();
        Assert.NotNull(c);
        var firstCatalog = c!.First(x => x.IsCatalogProduct);
        Assert.Equal("BUYER-RES", firstCatalog.SupplierItemCode);
    }

    [Fact]
    public async Task AboveThreshold_IndexedReturnsNull_FallsBackToInMemoryPath()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId, ("ES-RES-220R", "Resistor 220 ohm 0.25W", null));

        // Indexed selected, but RetrieveCandidatesAsync returns null (provider can't translate) →
        // the service must fall back to the in-memory load+score path and still ground candidates.
        var fake = new FakeCatalogRetrievalService { UseIndexed = true, Retrieved = null };

        var (aiMock, captured) = CaptureAiMock();
        var (svc, _) = Build(db, aiMock.Object, fake);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(fake.RetrieveCalled);

        var c = captured();
        Assert.NotNull(c);
        // The in-memory path retrieved the resistor from the DB catalog — fallback works.
        Assert.Contains(c!, x => x.IsCatalogProduct && x.SupplierItemCode == "ES-RES-220R");
    }

    [Fact]
    public async Task BelowThreshold_KeepsInMemoryPath_DoesNotInvokeIndexedRetrieval()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId, ("ES-RES-220R", "Resistor 220 ohm 0.25W", null));

        // Small catalog → ShouldUseIndexedRetrievalAsync returns false → in-memory path; the
        // indexed RetrieveCandidatesAsync must NOT be called (byte-identical to today's behaviour).
        var fake = new FakeCatalogRetrievalService { UseIndexed = false };

        var (aiMock, captured) = CaptureAiMock();
        var (svc, _) = Build(db, aiMock.Object, fake);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResistorCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(fake.ShouldUseCalled);
        Assert.False(fake.RetrieveCalled);

        var c = captured();
        Assert.NotNull(c);
        Assert.Contains(c!, x => x.IsCatalogProduct && x.SupplierItemCode == "ES-RES-220R");
    }

    /// <summary>
    /// In-process stand-in for the Postgres-only <see cref="ICatalogRetrievalService"/>. Records
    /// whether each method was invoked so tests can assert the threshold switch / no-call contract.
    /// </summary>
    private sealed class FakeCatalogRetrievalService : ICatalogRetrievalService
    {
        public bool UseIndexed { get; set; }
        public IReadOnlyList<SupplierProduct>? Retrieved { get; set; }
        public bool ShouldUseCalled { get; private set; }
        public bool RetrieveCalled { get; private set; }

        public Task<bool> ShouldUseIndexedRetrievalAsync(Guid orgId, Guid supplierId, int inMemoryThreshold, CancellationToken ct)
        {
            ShouldUseCalled = true;
            return Task.FromResult(UseIndexed);
        }

        public Task<IReadOnlyList<SupplierProduct>?> RetrieveCandidatesAsync(
            Guid orgId, Guid supplierId, IReadOnlyList<CatalogRetrievalQuery> queries,
            int perQueryTopK, int overallCap, CancellationToken ct)
        {
            RetrieveCalled = true;
            return Task.FromResult(Retrieved);
        }
    }
}
