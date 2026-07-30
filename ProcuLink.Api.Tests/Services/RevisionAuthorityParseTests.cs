using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch batch 7 — the pinned revision's PARSE-side snapshots (input mapping + item mappings)
/// must govern <c>ParseStoredFileAsync</c> when the <c>Connections:RevisionAuthority</c> flag is
/// ON, while the live tables stay free to diverge.
///
/// Two layers of coverage:
/// <list type="bullet">
///   <item><b>Snapshot semantics</b> — the internal helpers (<c>BuildLineEntitiesAsync</c> with a
///   snapshot, <c>ResolveSnapshotPoMapping</c>, <c>ResolveFromSnapshot</c>) are unit-tested
///   directly (InternalsVisibleTo). The item-code contract has two halves that differ on purpose:
///   <b>Ordinal result keys</b> (each requested spelling gets its own answer) with a
///   <b>case-insensitive match</b> against stored rows (<c>ItemCodeComparison</c>), plus
///   unresolved→review. <c>ResolveFromSnapshot</c> is compared against
///   <c>ItemMappingService.ResolveManyAsync</c>'s ACTUAL answers rather than a restated
///   expectation, so neither path can change its case rule alone.</item>
///   <item><b>Wiring proof</b> — <c>ParseStoredFileAsync</c> is driven end-to-flush and the LIVE
///   services (<c>IPoMappingService.GetAsync</c>, <c>IItemMappingService.ResolveManyAsync</c>) are
///   verified NEVER consulted for a pinned order with usable snapshots — and consulted exactly as
///   today when the flag is off. (The success path's final ExecuteUpdateAsync is untranslatable on
///   the EF InMemory provider, so the wiring proof asserts on interactions that all happen BEFORE
///   that persistence step; full-path on real Postgres is the live-cutover gate.)</item>
/// </list>
/// </summary>
public class RevisionAuthorityParseTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── Construction helpers ───────────────────────────────────────────────────

    private static OrderIngestionService BuildIngestion(
        ProcuLinkDbContext db,
        IItemMappingService itemMappings,
        IPoMappingService poMappings,
        IFileStorageService? fileStorage = null,
        IEffectiveConnectionConfigResolver? effectiveConfig = null)
    {
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        return new OrderIngestionService(
            db,
            fileStorage ?? new Mock<IFileStorageService>().Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            itemMappings,
            poMappings,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            structuredExtractor: null,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: effectiveConfig);
    }

    private static List<ParsedOrderLine> Lines(params string[] buyerCodes) =>
        buyerCodes.Select((code, i) => new ParsedOrderLine(
            LineNumber: i + 1, BuyerItemCode: code, Description: $"Item {code}",
            Quantity: 1m, Unit: "EA", UnitPrice: 10m)).ToList();

    // ── Snapshot semantics: item-code resolution ───────────────────────────────

    [Fact]
    public async Task BuildLineEntities_SnapshotProvided_SnapshotWins_LiveTableIgnored()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // LIVE table maps B-1 → LIVE-1 (edited after publish) — must be ignored.
        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "B-1", SupplierItemCode = "LIVE-1", Confidence = 1f,
            Source = "manual", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ingestion = BuildIngestion(db, new ItemMappingService(db), new Mock<IPoMappingService>().Object);

        var snapshot = new[] { new EffectiveRevisionItemMapping("B-1", "REV-1") };
        var entities = await ingestion.BuildLineEntitiesAsync(
            orgId, supplierId, "Supplier", Lines("B-1"),
            Array.Empty<AiMappingCandidate>(), CancellationToken.None, snapshot);

        var line = Assert.Single(entities);
        Assert.Equal("REV-1", line.SupplierItemCode); // the revision snapshot, NOT the live LIVE-1
        Assert.False(line.NeedsReview);
    }

    [Fact]
    public async Task BuildLineEntities_CodeOutsideSnapshot_GoesToReview_EvenThoughLiveTableMapsIt()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // The LIVE table could resolve B-2, but the pinned snapshot does not contain it —
        // under revision authority the line goes to review (exactly like an unmapped code today).
        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "B-2", SupplierItemCode = "LIVE-2", Confidence = 1f,
            Source = "manual", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ingestion = BuildIngestion(db, new ItemMappingService(db), new Mock<IPoMappingService>().Object);

        var snapshot = new[] { new EffectiveRevisionItemMapping("B-1", "REV-1") };
        var entities = await ingestion.BuildLineEntitiesAsync(
            orgId, supplierId, "Supplier", Lines("B-1", "B-2"),
            Array.Empty<AiMappingCandidate>(), CancellationToken.None, snapshot);

        Assert.Equal("REV-1", entities.Single(l => l.BuyerItemCode == "B-1").SupplierItemCode);
        var unresolved = entities.Single(l => l.BuyerItemCode == "B-2");
        Assert.Null(unresolved.SupplierItemCode);
        Assert.True(unresolved.NeedsReview);
    }

    [Fact]
    public async Task BuildLineEntities_NoSnapshot_LiveTableResolves_GoldenBehaviour()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "B-1", SupplierItemCode = "LIVE-1", Confidence = 1f,
            Source = "manual", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ingestion = BuildIngestion(db, new ItemMappingService(db), new Mock<IPoMappingService>().Object);

        var entities = await ingestion.BuildLineEntitiesAsync(
            orgId, supplierId, "Supplier", Lines("B-1"),
            Array.Empty<AiMappingCandidate>(), CancellationToken.None, itemMappingSnapshot: null);

        Assert.Equal("LIVE-1", Assert.Single(entities).SupplierItemCode);
    }

    /// <summary>
    /// Seeds the LIVE item_mappings table with the given rows, one minute apart in the order given,
    /// so the live resolver's "most recently updated" tie-break is well-defined rather than
    /// depending on insertion order.
    /// </summary>
    private static async Task<(ItemMappingService Live, Guid OrgId, Guid SupplierId)> SeedLiveMappingsAsync(
        ProcuLinkDbContext db, params (string BuyerCode, string SupplierCode)[] rows)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var stamp      = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var (buyerCode, supplierCode) in rows)
        {
            db.ItemMappings.Add(new ItemMapping
            {
                Id               = Guid.NewGuid(),
                OrgId            = orgId,
                SupplierId       = supplierId,
                BuyerItemCode    = buyerCode,
                SupplierItemCode = supplierCode,
                Source           = "manual",
                Confidence       = 1f,
                CreatedAt        = stamp,
                UpdatedAt        = stamp,
            });
            stamp = stamp.AddMinutes(1);
        }

        await db.SaveChangesAsync();
        return (new ItemMappingService(db), orgId, supplierId);
    }

    [Fact]
    public async Task ResolveFromSnapshot_MirrorsResolveManySemantics_TrimmedOrdinalKeys_CaseInsensitiveMatch()
    {
        // The contract has TWO halves and they differ on purpose:
        //   • the MATCH against stored rows folds case (ItemCodeComparison), because the supplier
        //     CATALOG always did, and a resolver that disagreed with it made a buyer whose ERP
        //     changed its export casing silently lose every mapping their operators had taught;
        //   • the KEYS are Ordinal, because "B-1" and "b-1" may be two different LINES on one order
        //     and each must get its own answer. Folding the keys made the dictionary answer the
        //     second line from the first line's row — the WRONG ITEM ordered, with no review flag.
        //
        // This test's OLD name said "TrimmedCaseInsensitiveKeys" and its body asserted exactly that,
        // which is why the batch resolver's collapse went unnoticed for so long. A test whose name
        // claims a MIRROR must verify the mirror, so it no longer restates what ResolveManyAsync
        // ought to return — it seeds the live table with the same rows and compares the two
        // resolvers' ACTUAL answers, key for key. If either path changes its case rule alone, the
        // comparison fails even though each would still look reasonable on its own.
        await using var db = NewDb();

        // Case-variant twins are a REAL production state, not a contrived one: the unique index on
        // (org_id, supplier_id, buyer_item_code) is case-SENSITIVE on Postgres's default collation,
        // so both rows are legal. "b-2" is stored ONLY in lower case.
        var (live, orgId, supplierId) = await SeedLiveMappingsAsync(db,
            ("B-1", "REV-UPPER"),
            ("b-1", "REV-LOWER"),
            ("b-2", "REV-LOWER-ONLY"));

        var snapshot = new[]
        {
            new EffectiveRevisionItemMapping("B-1", "REV-UPPER"),
            new EffectiveRevisionItemMapping("b-1", "REV-LOWER"),
            new EffectiveRevisionItemMapping("b-2", "REV-LOWER-ONLY"),
        };

        // Padding, both spellings of one code, a code stored only in lower case, an unknown code,
        // and the blanks the contract says are skipped.
        var lines = Lines("  B-1  ", "b-1", "B-2", "B-9", "", "   ");

        var fromSnapshot = OrderIngestionService.ResolveFromSnapshot(snapshot, lines);
        var fromLive     = await live.ResolveManyAsync(
            orgId, supplierId, lines.Select(l => l.BuyerItemCode), CancellationToken.None);

        // THE mirror assertion — the whole dictionaries, keys and values, compared to each other.
        Assert.Equal(
            fromLive.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            fromSnapshot.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());

        // …and non-vacuously: two matching empties would satisfy the comparison above, so pin the
        // shared answer as the RIGHT one.
        Assert.Equal(4, fromSnapshot.Count);                  // blanks skipped; "B-1" and "b-1" distinct
        Assert.Equal("REV-UPPER", fromSnapshot["B-1"]);        // exact-case wins over the twin…
        Assert.Equal("REV-LOWER", fromSnapshot["b-1"]);        // …and the other line gets ITS OWN row
        Assert.Equal("REV-LOWER-ONLY", fromSnapshot["B-2"]);   // the MATCH still folds case
        Assert.Null(fromSnapshot["B-9"]);                      // unknown → present-but-null → review
        Assert.False(fromSnapshot.ContainsKey("b-2"),
            "keys are the caller's exact spelling; a spelling nobody asked about is absent, not aliased");

        // Snapshot-ONLY: a snapshot is a copied LIST and can carry one spelling twice, which the
        // live table cannot (the unique index forbids it) — so there is nothing to compare against
        // here. Last row wins, the snapshot's deterministic stand-in for "most recently updated".
        var duplicated = OrderIngestionService.ResolveFromSnapshot(
            new[]
            {
                new EffectiveRevisionItemMapping("B-7", "REV-OLD"),
                new EffectiveRevisionItemMapping("B-7", "REV-NEW"),
            },
            Lines("B-7"));

        Assert.Equal("REV-NEW", duplicated["B-7"]);
    }

    // ── Snapshot semantics: parse mapping ──────────────────────────────────────

    [Fact]
    public void ResolveSnapshotPoMapping_UsableSnapshot_ReturnsConfig()
    {
        using var db = NewDb();
        var ingestion = BuildIngestion(db, new Mock<IItemMappingService>().Object, new Mock<IPoMappingService>().Object);

        var inputJson = JsonSerializer.Serialize(new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "ref" } },
            Lines  = { ["BuyerItemCode"] = new FieldMappingEntry { ExternalField = "sku" } },
        }, CamelCase);

        var effective = new EffectiveConnectionConfig { RevisionId = Guid.NewGuid(), InputMappingJson = inputJson };
        var config = ingestion.ResolveSnapshotPoMapping(effective, Guid.NewGuid());

        Assert.NotNull(config);
        Assert.Equal("ref", config!.Header["PoNumber"].ExternalField);
        Assert.Equal("sku", config.Lines["BuyerItemCode"].ExternalField);
    }

    [Theory]
    [InlineData(null)]                  // nothing snapshotted (backfilled rev-1 without a PO mapping)
    [InlineData("")]                    // blank
    [InlineData("{ not valid json")]    // malformed — logged, never a throw
    [InlineData("{\"header\":{},\"lines\":{}}")] // empty config — not usable
    public void ResolveSnapshotPoMapping_UnusableSnapshot_ReturnsNull_LiveFallback(string? inputJson)
    {
        using var db = NewDb();
        var ingestion = BuildIngestion(db, new Mock<IItemMappingService>().Object, new Mock<IPoMappingService>().Object);

        var effective = new EffectiveConnectionConfig { RevisionId = Guid.NewGuid(), InputMappingJson = inputJson };
        Assert.Null(ingestion.ResolveSnapshotPoMapping(effective, Guid.NewGuid()));
    }

    [Fact]
    public void ResolveSnapshotPoMapping_LiveBundle_ReturnsNull()
    {
        using var db = NewDb();
        var ingestion = BuildIngestion(db, new Mock<IItemMappingService>().Object, new Mock<IPoMappingService>().Object);
        Assert.Null(ingestion.ResolveSnapshotPoMapping(EffectiveConnectionConfig.Live, Guid.NewGuid()));
    }

    // ── Wiring proof through ParseStoredFileAsync ──────────────────────────────

    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedParsingOrderAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "ParseCo", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-PENDING", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
            Status = "parsing", SourceFileKey = $"{orgId}/{orderId}/order.csv",
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    private static async Task<SupplierConnectionRevision> SeedParseRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, Guid orderId)
    {
        // Input snapshot matching the test CSV columns (ref/sku/qty/price).
        var inputJson = JsonSerializer.Serialize(new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "ref" } },
            Lines =
            {
                ["LineNumber"]    = new FieldMappingEntry { ExternalField = "line" },
                ["BuyerItemCode"] = new FieldMappingEntry { ExternalField = "sku" },
                ["Quantity"]      = new FieldMappingEntry { ExternalField = "qty" },
                ["UnitPrice"]     = new FieldMappingEntry { ExternalField = "price" },
            },
        }, CamelCase);

        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", CreatedAt = now, PublishedAt = now,
            InputMappingJson = inputJson,
            ItemMappings =
            {
                // The snapshot maps WIDGET only — GIZMO must flow to review under authority.
                new ConnectionRevisionItemMapping
                {
                    Id = Guid.NewGuid(), BuyerItemCode = "WIDGET", SupplierItemCode = "REV-W",
                    Confidence = 1f, Source = "manual",
                },
            },
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revision.Id;
        await db.SaveChangesAsync();
        return revision;
    }

    private static IFileStorageService CsvStorage() =>
        Mock.Of<IFileStorageService>(s =>
            s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
                == Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(
                    "ref,line,sku,qty,price\nPO-REV-9,1,WIDGET,2,9.99\nPO-REV-9,2,GIZMO,1,5.00\n"))));

    /// <summary>
    /// Runs ParseStoredFileAsync tolerating the EF InMemory provider's inability to translate the
    /// final ExecuteUpdateAsync persistence step — everything under test here (effective-config
    /// resolution, parse-mapping choice, item-code resolution, AI batching) happens BEFORE it.
    /// </summary>
    private static async Task RunParseToleratingInMemoryPersistenceAsync(
        OrderIngestionService ingestion, Guid orgId, Guid orderId)
    {
        try
        {
            await ingestion.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected on InMemory: ExecuteUpdateAsync is untranslatable. The interactions
            // asserted by the caller all occurred before this point.
        }
    }

    [Fact]
    public async Task ParseStoredFile_FlagOnPinned_RevisionSnapshotsGovern_LiveServicesNeverConsulted()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedParsingOrderAsync(db);
        await SeedParseRevisionAsync(db, orgId, supplierId, orderId);

        // LIVE services (would map BOTH codes / provide a different live PO mapping) — must stay unconsulted.
        var liveItemMappings = new Mock<IItemMappingService>();
        liveItemMappings
            .Setup(s => s.ResolveManyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["WIDGET"] = "LIVE-W",
                ["GIZMO"]  = "LIVE-G",
            });
        var livePoMappings = new Mock<IPoMappingService>();
        livePoMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        IReadOnlyList<AiMappingLineContext>? aiUnresolved = null;
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, unresolved, _, _) => aiUnresolved = unresolved)
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var ingestion = new OrderIngestionService(
            db,
            CsvStorage(),
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            liveItemMappings.Object,
            livePoMappings.Object,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            structuredExtractor: null,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: Resolver(db, enabled: true));

        await RunParseToleratingInMemoryPersistenceAsync(ingestion, orgId, orderId);

        // The revision's INPUT snapshot governed the parse — the live PO mapping was never read.
        livePoMappings.Verify(
            s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The revision's ITEM snapshot governed resolution — the live table was never queried.
        liveItemMappings.Verify(
            s => s.ResolveManyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // POSITIVE proof the snapshot resolved WIDGET (and only GIZMO stayed unresolved): the
        // single batched AI call received exactly the GIZMO line — under the live mocks BOTH
        // codes would have resolved and the AI call would not have happened at all.
        Assert.NotNull(aiUnresolved);
        var unresolvedLine = Assert.Single(aiUnresolved!);
        Assert.Equal("GIZMO", unresolvedLine.BuyerItemCode);
    }

    [Fact]
    public async Task ParseStoredFile_FlagOff_PinnedOrder_LiveServicesConsultedExactlyAsToday()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedParsingOrderAsync(db);
        await SeedParseRevisionAsync(db, orgId, supplierId, orderId); // pinned, but the flag is OFF

        var liveItemMappings = new Mock<IItemMappingService>();
        liveItemMappings
            .Setup(s => s.ResolveManyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["PO-REV-9"] = "X", // keys irrelevant — flag-off parse uses the default CSV parser's codes
            });
        var livePoMappings = new Mock<IPoMappingService>();
        livePoMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var ingestion = BuildIngestion(
            db, liveItemMappings.Object, livePoMappings.Object,
            fileStorage: CsvStorage(),
            effectiveConfig: Resolver(db, enabled: false));

        await RunParseToleratingInMemoryPersistenceAsync(ingestion, orgId, orderId);

        // Byte-identical pre-batch-7 behaviour: the live services drive parse + resolution.
        livePoMappings.Verify(
            s => s.GetAsync(orgId, supplierId, It.IsAny<CancellationToken>()),
            Times.Once);
        liveItemMappings.Verify(
            s => s.ResolveManyAsync(orgId, supplierId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
