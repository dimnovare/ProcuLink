using FluentAssertions;
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
/// A supplier item code found by a DETERMINISTIC lookup carries no confidence, because nothing
/// measured it.
///
/// <para><b>The defect.</b> <c>OrderIngestionService</c> had two producers that both stamped a
/// literal <c>Confidence: 0.95f</c>:</para>
/// <list type="bullet">
///   <item>an EXACT match of the line's manufacturer part number against the supplier's own
///   catalog — one indexed query, no model;</item>
///   <item>a literal ECHO of a manufacturer part number the source document itself prints — not
///   even a lookup.</item>
/// </list>
///
/// <para>Neither invoked a model. Both bypassed the AI confidence floor, which sits in the
/// <c>else</c> arm they never reach. The number persisted to
/// <c>PurchaseOrderLineEntity.AiSuggestionConfidence</c>, was copied into <c>line.Confidence</c> on
/// accept, reached the order passport, and rendered in the review UI as "AI confidence 95%" in the
/// violet reserved for AI-generated content. It misdescribed the thing twice over: not AI, and not
/// a confidence — a catalog hit is a FACT about the supplier's configuration, and 95% both
/// understates it and misattributes it.</para>
///
/// <para>This is the same defect PR #202 fixed on the saved-mapping path, surviving in a second
/// place, so these tests are written to fail loudly on the specific literal rather than on a range.
/// The control at the bottom pins that a GENUINE model suggestion is untouched — the fix must
/// remove fabricated numbers, not confidence reporting.</para>
/// </summary>
public class DeterministicSuggestionsCarryNoConfidenceTests
{
    private const string SupplierCodeForLitwareScanner = "FAB-SCAN-77120";
    private const string LitwareManufacturerPart = "LTQ2500-BK-BTK1";

    // ── The two deterministic producers ───────────────────────────────────────

    [Fact]
    public async Task CatalogManufacturerPartMatch_CarriesNoConfidence_BecauseNoModelRan()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId,
            (SupplierCodeForLitwareScanner, "QuickTrace LTQ2500 Bluetooth kit", LitwareManufacturerPart, "Litware"));

        var svc = Build(db, UnresolvedMappingsMock().Object, SilentAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, PunchoutFixture);

        // The suggestion itself is unchanged — this fix removes a number, never the suggestion.
        line.AiSuggestedSupplierItemCode.Should().Be(SupplierCodeForLitwareScanner);
        line.AiSuggestionProvenance.Should().Be("catalog: manufacturer part number");

        // THE ASSERTION. Not "less than 0.95", not "in a range" — absent. A catalog hit has no
        // probability to report, and the UI keys off exactly this null to withhold the AI chip.
        line.AiSuggestionConfidence.Should().BeNull(
            "an exact catalog lookup is a fact, not a measurement — it used to be stamped 0.95f, "
            + "which the review UI printed as \"AI confidence 95%\" in AI-violet");
    }

    [Fact]
    public async Task SourceDocumentPartNumberEcho_CarriesNoConfidence_BecauseNothingJudgedIt()
    {
        // No catalog at all → the service falls through to echoing the part number the document
        // states. This is the (c) branch: not a lookup, just a copy.
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var svc = Build(db, UnresolvedMappingsMock().Object, SilentAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, PunchoutFixture);

        line.AiSuggestedSupplierItemCode.Should().Be(LitwareManufacturerPart);
        line.AiSuggestionProvenance.Should().Be("source document: manufacturer part number");

        line.AiSuggestionConfidence.Should().BeNull(
            "echoing a code the document already prints judges nothing — it used to be stamped 0.95f");
    }

    // ── The control: a real model score must survive ──────────────────────────

    [Fact]
    public async Task ModelSuggestion_KeepsItsRealNumber()
    {
        // No catalog, and the line carries NO manufacturer part number, so neither deterministic
        // branch can fire and the AI path is the only source of a suggestion.
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var scoringAi = AiMockReturning(new AiMappingSuggestion(
            "SUP-FROM-MODEL", 0.82f, "fuzzy match on description", "OpenAI structured output"));

        var svc = Build(db, UnresolvedMappingsMock().Object, scoringAi.Object);
        var line = await IngestWithoutManufacturerPartAsync(svc, db, orgId, supplierId);

        line.AiSuggestedSupplierItemCode.Should().Be("SUP-FROM-MODEL");
        line.AiSuggestionConfidence.Should().BeApproximately(0.82f, 1e-4f,
            "a real model score is exactly what this column is FOR — the fix removes fabricated "
            + "numbers, and must not suppress measured ones");
    }

    [Fact]
    public async Task ModelSuggestionBelowTheFloor_IsStillDropped()
    {
        // Guards the floor comparison, which had to grow a null check when Confidence became
        // nullable. A weak fuzzy match is worse than no suggestion and must still disappear.
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var weakAi = AiMockReturning(new AiMappingSuggestion(
            "SUP-WEAK", 0.40f, "weak fuzzy match", "OpenAI structured output"));

        var svc = Build(db, UnresolvedMappingsMock().Object, weakAi.Object);
        var line = await IngestWithoutManufacturerPartAsync(svc, db, orgId, supplierId);

        line.AiSuggestedSupplierItemCode.Should().BeNull("0.40 is below the 0.65 suggestion floor");
        line.AiSuggestionConfidence.Should().BeNull();
    }

    [Fact]
    public async Task ScorerThatReturnedNoNumber_IsDroppedLikeABelowFloorScore()
    {
        // `null < floor` is false in C#, so an unscored suggestion arriving on the MODEL path would
        // sail past a floor it cannot be measured against. A scorer that returned no number has
        // told us nothing, which is not the same as clearing the bar.
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var unscoredAi = AiMockReturning(new AiMappingSuggestion(
            "SUP-UNSCORED", null, "no score returned", "OpenAI structured output"));

        var svc = Build(db, UnresolvedMappingsMock().Object, unscoredAi.Object);
        var line = await IngestWithoutManufacturerPartAsync(svc, db, orgId, supplierId);

        line.AiSuggestedSupplierItemCode.Should().BeNull(
            "an unscored suggestion on the scoring path cannot clear a confidence floor");
    }

    // ── Bulk accept: an unscored suggestion cannot clear a threshold ───────────

    [Fact]
    public async Task UnscoredDeterministicSuggestion_IsNotSweptUpByBulkAccept()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();
        await AddCatalogAsync(db, orgId, supplierId,
            (SupplierCodeForLitwareScanner, "QuickTrace LTQ2500 Bluetooth kit", LitwareManufacturerPart, "Litware"));

        var svc = Build(db, UnresolvedMappingsMock().Object, SilentAiMock().Object);
        var line = await IngestSingleLineAsync(svc, db, orgId, supplierId, PunchoutFixture);
        line.AiSuggestionConfidence.Should().BeNull("precondition");

        // "Accept everything the model was at least 90% sure about." Nothing here was sure of
        // anything, because nothing measured it. Before the fix this line arrived stamped 0.95 and
        // was swept up by any threshold at or below that — auto-applied on a number no model
        // produced. It now stays in review for a deliberate one-click accept.
        var accepted = await svc.AcceptAiSuggestionsAsync(orgId, line.OrderId, 0.90, CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue();
        accepted.Value.Should().Be(0, "a suggestion with no confidence cannot clear a confidence threshold");

        var after = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == line.Id);
        after.SupplierItemCode.Should().BeNull();
        after.NeedsReview.Should().BeTrue();
        after.Confidence.Should().BeNull(
            "nothing may be promoted into the line's confidence column from an unscored suggestion");
    }

    [Fact]
    public async Task ScoredSuggestionAboveTheThreshold_IsStillBulkAccepted()
    {
        // The control for the test above: bulk accept still works on real scores.
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var scoringAi = AiMockReturning(new AiMappingSuggestion(
            "SUP-FROM-MODEL", 0.93f, "strong match", "OpenAI structured output"));

        var svc = Build(db, UnresolvedMappingsMock().Object, scoringAi.Object);
        var line = await IngestWithoutManufacturerPartAsync(svc, db, orgId, supplierId);

        var accepted = await svc.AcceptAiSuggestionsAsync(orgId, line.OrderId, 0.90, CancellationToken.None);

        accepted.Value.Should().Be(1);
        var after = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == line.Id);
        after.SupplierItemCode.Should().Be("SUP-FROM-MODEL");
        after.Confidence.Should().BeApproximately(0.93f, 1e-4f, "the model's real number is promoted");
    }

    // ── Helpers (mirroring OrderServiceManufacturerPartMatchTests) ────────────

    /// <summary>Ariba punchout: buyer code is the network's internal id, MPN is the only real key.</summary>
    private const string PunchoutFixture = "real-cxml-1.2-ariba-punchout-mpn-differs.xml";

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
            Name = "Fabrikam",
            Slug = $"fabrikam-{orgId:N}",
            ClerkOrgId = $"org_{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Fabrikam Supply",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

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

    /// <summary>An AI service that suggests nothing, so any suggestion must be deterministic.</summary>
    private static Mock<IAiMappingService> SilentAiMock() =>
        AiMockReturning(null);

    /// <summary>An AI service returning <paramref name="suggestion"/> for line 1 (or nothing when null).</summary>
    private static Mock<IAiMappingService> AiMockReturning(AiMappingSuggestion? suggestion)
    {
        var map = new Dictionary<int, AiMappingSuggestion>();
        if (suggestion is not null) map[1] = suggestion;

        var ai = new Mock<IAiMappingService>();
        ai
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)map);
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

    private static Stream OpenFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"Fixture not copied to the test output: {path}");
        return File.OpenRead(path);
    }

    /// <summary>
    /// The punchout fixture with its <c>&lt;ManufacturerPartID&gt;</c> element removed.
    ///
    /// <para>Both deterministic producers key off the line's manufacturer part number, so a line
    /// that states none cannot reach either branch — which is the only way to exercise the AI path
    /// in isolation. Stripping it from the real order beats hand-rolling a second fixture: the
    /// document stays byte-identical everywhere else, so a test that passes here is testing the
    /// absence of the part number and nothing else.</para>
    /// </summary>
    private static Stream OpenFixtureWithoutManufacturerPart(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"Fixture not copied to the test output: {path}");

        var xml = File.ReadAllText(path);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            xml, @"<ManufacturerPartID>.*?</ManufacturerPartID>", string.Empty);

        Assert.DoesNotContain("<ManufacturerPartID>", stripped, StringComparison.Ordinal);
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(stripped));
    }

    /// <summary>Ingests the punchout fixture with its manufacturer part number stripped, so the
    /// AI path is the only possible source of a suggestion.</summary>
    private static async Task<PurchaseOrderLineEntity> IngestWithoutManufacturerPartAsync(
        OrderService svc, ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        await using var stream = OpenFixtureWithoutManufacturerPart(PunchoutFixture);
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, PunchoutFixture, "application/xml", CancellationToken.None);

        Assert.True(result.IsSuccess, $"Ingest failed: {result.Error}");
        var line = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.OrderId == result.Value!.Id);
        Assert.Null(line.ManufacturerPartNumber);
        return line;
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
}
