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
///   directly (InternalsVisibleTo). For item codes the tests pin the DECISION a buyer experiences —
///   which supplier code a given input returns, and whether a replay reaches the same row the live
///   run did — always by comparing against <c>ItemMappingService.ResolveManyAsync</c>'s ACTUAL
///   output rather than a restated literal. They name no comparer: a test written against the
///   mechanism goes stale the moment the mechanism moves, which is exactly what happened to the
///   test these replace.</item>
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

    // ── Snapshot resolution: the DECISION, not the mechanism ────────────────────
    //
    // These replace a single test called `ResolveFromSnapshot_MirrorsResolveManySemantics_
    // TrimmedCaseInsensitiveKeys`, whose name and body both encoded a MECHANISM — which comparer
    // keys the returned dictionary. D1 changed that mechanism deliberately (folding the keys made
    // one line receive another product's supplier code), so the test failed and its name had gone
    // stale silently. A test written against the mechanism has to be rewritten every time the
    // mechanism moves, which is the two-comparers-over-one-column defect biting inside its own fix.
    //
    // What matters to a buyer is the DECISION: for a given input code, which supplier code comes
    // back — and does a replay reach the same one the live run did. Each test below pins one such
    // decision, and every one of them is asserted against the live resolver's ACTUAL output rather
    // than a restated literal, so neither path can drift no matter which comparer either uses
    // internally. None of them mentions a comparer.

    /// <summary>
    /// Seeds the LIVE table AND builds the equivalent revision snapshot from the same rows, so the
    /// two paths are given identical inputs by construction rather than by two hand-kept literals.
    /// Rows are stamped one minute apart in the order given, so the live resolver's "most recently
    /// updated" tie-break is well-defined instead of depending on insertion order.
    /// </summary>
    private static async Task<(ItemMappingService Live, Guid OrgId, Guid SupplierId, EffectiveRevisionItemMapping[] Snapshot)>
        SeedBothPathsAsync(ProcuLinkDbContext db, params (string BuyerCode, string SupplierCode)[] rows)
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

        var snapshot = rows
            .Select(r => new EffectiveRevisionItemMapping(r.BuyerCode, r.SupplierCode))
            .ToArray();

        return (new ItemMappingService(db), orgId, supplierId, snapshot);
    }

    /// <summary>Resolves the same input codes through both paths and returns the two answers.</summary>
    private static async Task<(IReadOnlyDictionary<string, string?> Snapshot, IReadOnlyDictionary<string, string?> Live)>
        ResolveBothWaysAsync(
            ItemMappingService live, Guid orgId, Guid supplierId,
            EffectiveRevisionItemMapping[] snapshot, params string[] inputCodes)
    {
        var lines = Lines(inputCodes);
        return (
            OrderIngestionService.ResolveFromSnapshot(snapshot, lines),
            await live.ResolveManyAsync(orgId, supplierId, lines.Select(l => l.BuyerItemCode), CancellationToken.None));
    }

    [Fact]
    public async Task TwoInputsDifferingOnlyInCase_EachGetTheirOwnSupplierCode_OnBothPaths()
    {
        // D1's whole point. An org may treat "B-1" and "b-1" as different products — the unique
        // index on buyer_item_code is case-SENSITIVE on Postgres's default collation, so both rows
        // are legal and this is a real production state. Collapsing the two inputs to one answer
        // ships the WRONG ITEM on the second line, confidently and with no review flag.
        await using var db = NewDb();
        var (live, orgId, supplierId, snapshot) = await SeedBothPathsAsync(db,
            ("B-1", "SUP-UPPER"),
            ("b-1", "SUP-LOWER"));

        var (fromSnapshot, fromLive) = await ResolveBothWaysAsync(
            live, orgId, supplierId, snapshot, "B-1", "b-1");

        // Two inputs → two DISTINCT answers, on both paths.
        Assert.Equal(2, fromLive.Count);
        Assert.Equal(2, fromSnapshot.Count);
        Assert.NotEqual(fromLive["B-1"], fromLive["b-1"]);

        // …and the two paths agree on which answer belongs to which input.
        Assert.Equal(fromLive["B-1"], fromSnapshot["B-1"]);
        Assert.Equal(fromLive["b-1"], fromSnapshot["b-1"]);

        // Non-vacuity: pin the answers, so two matching nulls cannot satisfy the above.
        Assert.Equal("SUP-UPPER", fromSnapshot["B-1"]);
        Assert.Equal("SUP-LOWER", fromSnapshot["b-1"]);
    }

    [Theory]
    [InlineData("WIDGET-1")]   // exact
    [InlineData("widget-1")]   // all lower
    [InlineData("Widget-1")]   // title
    [InlineData("wIdGeT-1")]   // mixed
    [InlineData("  WIDGET-1 ")]// padded
    [InlineData("NOT-STORED")] // unknown → both must return nothing
    public async Task OneInput_ResolvesToTheSameSupplierCode_LiveAndFromSnapshot(string input)
    {
        // D3. A replay renders through ResolveFromSnapshot; if it reached a different row than the
        // live run did, it is not a replay. Whatever the case rule is, both paths must apply it
        // identically — including to an input that resolves to nothing.
        await using var db = NewDb();
        var (live, orgId, supplierId, snapshot) = await SeedBothPathsAsync(db, ("WIDGET-1", "SUP-9"));

        var (fromSnapshot, fromLive) = await ResolveBothWaysAsync(
            live, orgId, supplierId, snapshot, input);

        var key = input.Trim();
        Assert.Equal(fromLive[key], fromSnapshot[key]);
    }

    [Fact]
    public async Task TheTieBreakAmongCaseVariantTwins_PicksTheSameRow_OnBothPaths()
    {
        // When one input matches SEVERAL stored rows, both paths must choose the same one, or a
        // replay of a delivered order silently substitutes a different supplier code. The rows are
        // ordered so a naive "last one wins" and the live resolver's choice DISAGREE — otherwise
        // this passes for the wrong reason.
        await using var db = NewDb();
        var (live, orgId, supplierId, snapshot) = await SeedBothPathsAsync(db,
            ("B-1", "SUP-UPPER"),   // the exact-case row, written FIRST (so "last wins" would miss it)
            ("b-1", "SUP-LOWER"));

        var (fromSnapshot, fromLive) = await ResolveBothWaysAsync(
            live, orgId, supplierId, snapshot, "B-1");

        Assert.Equal(fromLive["B-1"], fromSnapshot["B-1"]);
        Assert.Equal("SUP-UPPER", fromSnapshot["B-1"]);
    }

    [Fact]
    public async Task EveryNonBlankInputGetsAnAnswer_AndBlanksAreSkipped_OnBothPaths()
    {
        // The totality half of the contract: callers treat the result as total over the non-blank
        // inputs, so a missing key would throw instead of sending the line to review. An input
        // nobody supplied must NOT appear — inventing an alias for it is how one line's answer
        // reaches another line.
        await using var db = NewDb();
        var (live, orgId, supplierId, snapshot) = await SeedBothPathsAsync(db,
            ("B-1", "SUP-1"),
            ("b-2", "SUP-2"));

        var (fromSnapshot, fromLive) = await ResolveBothWaysAsync(
            live, orgId, supplierId, snapshot, "  B-1  ", "B-2", "B-9", "", "   ");

        // Whole-answer comparison: same keys, same values, both paths.
        Assert.Equal(
            fromLive.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            fromSnapshot.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());

        Assert.Equal(3, fromSnapshot.Count);                  // blanks skipped, padding trimmed
        Assert.Equal("SUP-1", fromSnapshot["B-1"]);
        Assert.Equal("SUP-2", fromSnapshot["B-2"]);           // matching still ignores case
        Assert.Null(fromSnapshot["B-9"]);                     // unknown → present-but-null → review
        Assert.False(fromSnapshot.ContainsKey("b-2"),
            "no line asked about 'b-2'; answering a question nobody posed is how one line's answer "
            + "reaches another line");
    }

    [Fact]
    public async Task ASnapshotCarryingOneSpellingTwice_TakesTheLastRow()
    {
        // Snapshot-ONLY, and deliberately not compared against the live path: a snapshot is a copied
        // LIST and can hold the same spelling twice, which the live table cannot (the unique index
        // forbids it). There is nothing to mirror, so this pins only that the choice is DEFINED —
        // an undefined one would make a replay's output depend on row order.
        await using var db = NewDb();
        var (_, _, _, _) = await SeedBothPathsAsync(db, ("B-7", "IRRELEVANT"));

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
