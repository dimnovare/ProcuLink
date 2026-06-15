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
/// T4 — external web/product-code grounding. Verifies that
/// <see cref="OrderIngestionService"/> consults <see cref="IProductCodeSearch"/> ONLY for
/// residual unresolved lines (a description, no source manufacturer part number) when the
/// supplier has NO authoritative catalog, and folds any hit in as a non-catalog candidate
/// ("web product search (unverified)") that is never auto-applied. With a catalog present —
/// or when the org is at its monthly AI cap — no web search runs.
/// </summary>
public class OrderServiceWebProductSearchTests
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
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Web Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

    private static async Task AddCatalogAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId, string code, string? name)
    {
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = code, Name = name, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

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
            .Setup(s => s.ResolveManyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["BUYER-CASE"] = null });
        return itemMappings;
    }

    private static OrderService Build(
        ProcuLinkDbContext db,
        IItemMappingService itemMappings,
        IAiMappingService aiMappings,
        IProductCodeSearch? productCodeSearch,
        IAiUsageTracker? aiUsage = null)
    {
        var poMappings = new Mock<IPoMappingService>();
        poMappings.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger.Setup(s => s.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            FileStorageMock().Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            itemMappings,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            productCodeSearch: productCodeSearch,
            aiUsage: aiUsage);
    }

    // One unresolved line, a clearly real product, no manufacturer part number in the source.
    private const string CaseCsv =
        "itemcode,description,quantity,price\n" +
        "BUYER-CASE,Apple iPhone 15 silicone case midnight,5,12.00\n";

    private static Mock<IAiMappingService> AiMock(
        IReadOnlyDictionary<int, AiMappingSuggestion> returns, List<AiMappingCandidate?> capture)
    {
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, _, candidates, _) => capture.AddRange(candidates))
            .ReturnsAsync(returns);
        return aiMappings;
    }

    [Fact]
    public async Task NoCatalog_NoSourceMpn_FoldsWebSearchCandidate_AndSurfacesAsSuggestion()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync(); // no catalog

        var search = new Mock<IProductCodeSearch>();
        search
            .Setup(s => s.FindPartNumberAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductCodeMatch("REDACTED-ORDER-DATA", "Apple iPhone 15 Silicone Case", "https://example.invalid/redacted", 0.7f));

        var captured = new List<AiMappingCandidate?>();
        // The model picks the web-grounded code (no catalog → free path, allow-list inert).
        var ai = AiMock(
            new Dictionary<int, AiMappingSuggestion>
            {
                [1] = new AiMappingSuggestion("REDACTED-ORDER-DATA", 0.7f, "found via web search",
                    "web product search (unverified): https://example.invalid/redacted"),
            },
            captured);

        var svc = Build(db, UnresolvedMappingsMock().Object, ai.Object, search.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CaseCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // The web hit was folded in as a non-catalog candidate with honest provenance.
        Assert.Contains(captured, c => c is { IsCatalogProduct: false, SupplierItemCode: "REDACTED-ORDER-DATA" }
                                        && c!.Provenance.Contains("web product search"));

        // It surfaces as a review hint only — never auto-applied.
        var line = await db.PurchaseOrderLines.AsNoTracking().FirstAsync(l => l.OrderId == result.Value!.Id);
        Assert.Equal("REDACTED-ORDER-DATA", line.AiSuggestedSupplierItemCode);
        Assert.True(line.NeedsReview);
        Assert.Null(line.SupplierItemCode);

        search.Verify(s => s.FindPartNumberAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WithCatalog_DoesNotRunWebSearch()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId, "ACME-CASE-15", "Phone case 15");

        var search = new Mock<IProductCodeSearch>(MockBehavior.Strict); // throws if called

        var captured = new List<AiMappingCandidate?>();
        var ai = AiMock( new Dictionary<int, AiMappingSuggestion>(), captured);

        var svc = Build(db, UnresolvedMappingsMock().Object, ai.Object, search.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CaseCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        search.Verify(s => s.FindPartNumberAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // No web candidate folded in.
        Assert.DoesNotContain(captured, c => c!.Provenance.Contains("web product search"));
    }

    [Fact]
    public async Task OrgAtMonthlyCap_SkipsWebSearch()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync(); // no catalog

        var search = new Mock<IProductCodeSearch>(MockBehavior.Strict); // throws if called

        var tracker = new Mock<IAiUsageTracker>();
        tracker.Setup(t => t.IsAtOrOverLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var captured = new List<AiMappingCandidate?>();
        var ai = AiMock( new Dictionary<int, AiMappingSuggestion>(), captured);

        var svc = Build(db, UnresolvedMappingsMock().Object, ai.Object, search.Object, aiUsage: tracker.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CaseCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        search.Verify(s => s.FindPartNumberAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoEgressOrg_SkipsWebSearch()
    {
        // A no-egress org (Organisation.SelfHostedOcr=true) must never have a line description
        // sent to OpenAI — the whole AI-candidate block (which web search lives inside) is gated.
        var (db, orgId, supplierId) = await SeedSupplierAsync(); // no catalog
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "clerk-noegress", Name = "No-Egress Org", Slug = "no-egress-org",
            SelfHostedOcr = true,
        });
        await db.SaveChangesAsync();

        var search = new Mock<IProductCodeSearch>(MockBehavior.Strict); // throws if called

        var captured = new List<AiMappingCandidate?>();
        var ai = AiMock(new Dictionary<int, AiMappingSuggestion>(), captured);

        var svc = Build(db, UnresolvedMappingsMock().Object, ai.Object, search.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CaseCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        search.Verify(s => s.FindPartNumberAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoSearcherWired_BehavesUnchanged()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync(); // no catalog

        var captured = new List<AiMappingCandidate?>();
        var ai = AiMock( new Dictionary<int, AiMappingSuggestion>(), captured);

        var svc = Build(db, UnresolvedMappingsMock().Object, ai.Object, productCodeSearch: null);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CaseCsv));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        // No catalog + no searcher → candidate set carries nothing (byte-identical to today).
        Assert.DoesNotContain(captured, c => c!.Provenance.Contains("web product search"));
    }

    // ── Pure residual-selection seam (no DB, no network) ────────────────────────

    private static AiMappingLineContext Line(int n, string? desc) => new(n, $"BUY-{n}", desc, 1, "PCS");

    [Fact]
    public void SelectWebSearchResidualLines_IncludesOnlyDescribedNonMpnLines()
    {
        var lines = new[]
        {
            Line(1, "Apple iPhone 15 silicone case"), // described, no MPN → eligible
            Line(2, "   "),                            // blank description → skip
            Line(3, null),                             // null description → skip
            Line(4, "Logitech MX Master 3S"),          // described, but has source MPN → skip
        };
        var sourceMpn = new HashSet<int> { 4 };

        var residual = OrderIngestionService.SelectWebSearchResidualLines(lines, sourceMpn, cap: 5);

        Assert.Equal(new[] { 1 }, residual.Select(l => l.LineNumber).ToArray());
    }

    [Fact]
    public void SelectWebSearchResidualLines_CapsCount_PreservingOrder()
    {
        var lines = Enumerable.Range(1, 9).Select(n => Line(n, $"Real product {n}")).ToArray();

        var residual = OrderIngestionService.SelectWebSearchResidualLines(lines, new HashSet<int>(), cap: 5);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, residual.Select(l => l.LineNumber).ToArray());
    }
}
