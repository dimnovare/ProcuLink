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
/// Manufacturer part number as a first-class matching key, driven by two REAL customer purchase
/// orders (sanitised — see the fixture headers).
///
/// The founder's case: a punchout order's <c>&lt;SupplierPartID&gt;</c> is the buying network's own
/// internal id and resolves against nothing. The only usable key is
/// <c>&lt;ManufacturerPartID&gt;</c>. Before this change the service simply ECHOED that part number
/// back as the supplier item code at 0.95 confidence — which looks right in the Maersk fixture
/// (where the two identifiers happen to be the same string) and is flatly wrong in the KSB/Ariba
/// one (where "REDACTED-ORDER-DATA" is REDACTED-PARTY's number, not something the supplier sells under).
///
/// What is asserted here:
///   • KSB/Ariba (REDACTED-PARTY): the manufacturer part number is LOOKED UP in the catalog and the
///     suggestion is the supplier's OWN code for that product — never the manufacturer's;
///   • Maersk: the same path still works when supplier code == manufacturer code;
///   • normalisation: a feed that prints the part number without separators still matches;
///   • ambiguity: one manufacturer part under two supplier codes suggests NOTHING rather than
///     guessing;
///   • no catalog: behaviour is exactly as before (the source part number is echoed);
///   • the suggestion is never auto-applied — the line still needs review.
/// </summary>
public class OrderServiceManufacturerPartMatchTests
{
    // The supplier's own code for the REDACTED-PARTY scanner. Deliberately shares no substring with
    // the manufacturer part number, so a passing test cannot be an accident of fuzzy matching.
    private const string SupplierCodeForREDACTED-PARTYScanner = "REDACTED-ITEM";
    private const string REDACTED-PARTYManufacturerPart = "REDACTED-ORDER-DATA";

    // Maersk's line: supplier part id and manufacturer part id are the SAME string.
    private const string MaerskSharedPartNumber = "REDACTED-ORDER-DATA";

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedSupplierAsync()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            Name = "Markit",
            Slug = $"markit-{orgId:N}",
            // Unique per org: ClerkOrgId defaults to "" and carries a UNIQUE index. EF InMemory
            // ignores unique indexes, so a collision here would only ever surface on Postgres.
            ClerkOrgId = $"org_{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "REDACTED-NAME",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

    /// <summary>
    /// Adds catalog rows the way the import path does — including the normalised lookup key,
    /// which has exactly one production writer (<c>SupplierCatalogService.UpsertManyAsync</c>).
    /// </summary>
    private static async Task AddCatalogAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        params (string Code, string? Name, string? Mpn, string? Manufacturer)[] products)
    {
        foreach (var (code, name, mpn, manufacturer) in products)
        {
            db.SupplierProducts.Add(new SupplierProduct
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                Code = code,
                Name = name,
                ManufacturerPartNumber = mpn,
                ManufacturerPartNumberNormalized = ProcuLink.Core.Catalog.ProductKeyNormalizer.Normalize(mpn),
                ManufacturerName = manufacturer,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
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

    /// <summary>Every buyer item code in these fixtures is unresolved — that is the whole point.</summary>
    private static Mock<IItemMappingService> UnresolvedMappingsMock()
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, IEnumerable<string> codes, CancellationToken _) =>
                (IReadOnlyDictionary<string, string?>)codes
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(c => c.Trim(), _ => (string?)null, StringComparer.Ordinal));
        return itemMappings;
    }

    /// <summary>An AI service that would suggest a WRONG code if it were ever consulted.</summary>
    private static Mock<IAiMappingService> DecoyAiMock()
    {
        var ai = new Mock<IAiMappingService>();
        ai
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>
            {
                [1] = new AiMappingSuggestion("WRONG-FUZZY-CODE", 0.90f, "fuzzy", "ai"),
            });
        return ai;
    }

    private static OrderService Build(
        ProcuLinkDbContext db, IItemMappingService itemMappings, IAiMappingService aiMappings)
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
            FileStorageMock().Object,
            new OrderParserFactory(new IPurchaseOrderParser[]
            {
                new CxmlOrderParser(), new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser(),
            }),
            itemMappings,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>
    /// Fixtures live in the Transform test project (they are parser fixtures first) and are
    /// linked into this project's output — see ProcuLink.Api.Tests.csproj.
    /// </summary>
    private static Stream OpenFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"Fixture not copied to the test output: {path}");
        return File.OpenRead(path);
    }

    private static async Task<PurchaseOrderLineEntity> IngestSingleLineAsync(
        OrderService svc, ProcuLinkDbContext db, Guid orgId, Guid supplierId, string fixture)
    {
        await using var stream = OpenFixture(fixture);
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, fixture, "application/xml", CancellationToken.None);

        Assert.True(result.IsSuccess, $"Ingest failed: {result.Error}");
        return await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == result.Value!.Id);
    }

    // ── The founder's case ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KsbAribaPunchout_SuggestsTheSuppliersOwnCode_NotTheManufacturersPartNumber()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId,
            (SupplierCodeForREDACTED-PARTYScanner, "QuickScan REDACTED-ORDER-DATA Bluetooth kit", REDACTED-PARTYManufacturerPart, "REDACTED-PARTY"),
            ("REDACTED-ITEM", "QuickScan QBT2400 kit", "QBT2400-BK-BTK1", "REDACTED-PARTY"));

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        // The buyer code really is the punchout id, and it really does not resolve.
        Assert.Equal("29954596", line.BuyerItemCode);
        Assert.Null(line.SupplierItemCode);

        // THE ASSERTION: the supplier's own code, translated from the manufacturer part number.
        Assert.Equal(SupplierCodeForREDACTED-PARTYScanner, line.AiSuggestedSupplierItemCode);
        Assert.NotEqual(REDACTED-PARTYManufacturerPart, line.AiSuggestedSupplierItemCode);

        // Sourced from the catalog, not from an AI guess and not from a bare echo.
        Assert.Contains("manufacturer part number", line.AiSuggestionProvenance!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog", line.AiSuggestionProvenance!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(REDACTED-PARTYManufacturerPart, line.AiSuggestionReason!);

        // Suggestion, never a silent rewrite — the operator still accepts it.
        Assert.True(line.NeedsReview);
    }

    [Fact]
    public async Task KsbAribaPunchout_CapturesManufacturerPartNumberAndName_OnTheLine()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        Assert.Equal(REDACTED-PARTYManufacturerPart, line.ManufacturerPartNumber);
        Assert.Equal("REDACTED-PARTY", line.ManufacturerName);
    }

    // ── The easy case that used to hide the bug ───────────────────────────────────────────

    [Fact]
    public async Task MaerskOrder_WhereSupplierCodeEqualsManufacturerCode_StillResolvesFromTheCatalog()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId,
            (MaerskSharedPartNumber, "Zebra ZT410 300 dpi printhead", MaerskSharedPartNumber, "Zebra"));

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.1-mpn-equals-supplier-part.xml");

        Assert.Equal(MaerskSharedPartNumber, line.AiSuggestedSupplierItemCode);
        Assert.Contains("catalog", line.AiSuggestionProvenance!, StringComparison.OrdinalIgnoreCase);
        Assert.True(line.NeedsReview);
    }

    [Fact]
    public async Task MaerskOrder_MatchesOnTheSupplierCodeEvenWhenTheCatalogCarriesNoManufacturerPartNumber()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // The catalog row has NO manufacturer part number — only the supplier code, which happens
        // to equal it. This is the common shape of a distributor feed that predates MPN support.
        await AddCatalogAsync(db, orgId, supplierId,
            (MaerskSharedPartNumber, "Zebra ZT410 300 dpi printhead", null, null));

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.1-mpn-equals-supplier-part.xml");

        Assert.Equal(MaerskSharedPartNumber, line.AiSuggestedSupplierItemCode);
        Assert.Contains("catalog", line.AiSuggestionProvenance!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Normalisation, ambiguity, and the unchanged no-catalog path ───────────────────────

    [Fact]
    public async Task ManufacturerPartNumber_MatchesAcrossSeparatorAndCaseDifferences()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // The catalog prints the part number with no separators and in lower case; the order
        // prints it as "REDACTED-ORDER-DATA". Same product.
        await AddCatalogAsync(db, orgId, supplierId,
            (SupplierCodeForREDACTED-PARTYScanner, "QuickScan REDACTED-ORDER-DATA Bluetooth kit", "qbt2500 bk btk1", "REDACTED-PARTY"));

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        Assert.Equal(SupplierCodeForREDACTED-PARTYScanner, line.AiSuggestedSupplierItemCode);
    }

    [Fact]
    public async Task AmbiguousManufacturerPartNumber_SuggestsNothingRatherThanGuessing()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // The same manufacturer part sold under two supplier codes (bare unit vs kit). There is
        // no honest way to choose, so no confident suggestion may be shown.
        await AddCatalogAsync(db, orgId, supplierId,
            (SupplierCodeForREDACTED-PARTYScanner, "QuickScan REDACTED-ORDER-DATA kit", REDACTED-PARTYManufacturerPart, "REDACTED-PARTY"),
            ("REDACTED-ITEM", "QuickScan REDACTED-ORDER-DATA kit (bundle)", REDACTED-PARTYManufacturerPart, "REDACTED-PARTY"));

        // The AI is stubbed to return nothing, so a suggestion could only come from the MPN path.
        var silentAi = new Mock<IAiMappingService>();
        silentAi
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var svc = Build(db, UnresolvedMappingsMock().Object, silentAi.Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        Assert.Null(line.AiSuggestedSupplierItemCode);
        Assert.True(line.NeedsReview);
    }

    [Fact]
    public async Task NoCatalog_KeepsTodaysBehaviour_EchoesTheSourceManufacturerPartNumber()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        // No catalog rows at all — nothing to translate against.

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        Assert.Equal(REDACTED-PARTYManufacturerPart, line.AiSuggestedSupplierItemCode);
        Assert.Contains("source document", line.AiSuggestionProvenance!, StringComparison.OrdinalIgnoreCase);
        Assert.True(line.NeedsReview);
    }

    [Fact]
    public async Task ManufacturerPartMatch_IsScopedToTheOrgAndSupplier()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        // Another org's supplier stocks the very same manufacturer part. It must never leak.
        var otherOrgId = Guid.NewGuid();
        var otherSupplierId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = otherOrgId, Name = "Other", Slug = $"other-{otherOrgId:N}",
            ClerkOrgId = $"org_{otherOrgId:N}", CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = otherSupplierId, OrgId = otherOrgId, Name = "Other Distribution", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await AddCatalogAsync(db, otherOrgId, otherSupplierId,
            ("LEAKED-CODE", "QuickScan REDACTED-ORDER-DATA kit", REDACTED-PARTYManufacturerPart, "REDACTED-PARTY"));

        var svc = Build(db, UnresolvedMappingsMock().Object, DecoyAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, "real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        Assert.NotEqual("LEAKED-CODE", line.AiSuggestedSupplierItemCode);
        // With no catalog of its own, this org falls back to the unchanged source-echo behaviour.
        Assert.Equal(REDACTED-PARTYManufacturerPart, line.AiSuggestedSupplierItemCode);
    }
}
