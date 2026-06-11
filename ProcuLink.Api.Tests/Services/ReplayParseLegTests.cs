using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Replay flip A — the opt-in PARSE-FROM-SOURCE replay leg. Verifies:
/// (1) GOLDEN — with the flag off (the default) the replay response is byte-identical to the
///     pre-flip contract (no <c>parseLeg</c> field serialized, storage never touched);
/// (2) a CSV order re-parsed under a DIFFERENT revision InputMappingJson surfaces header /
///     code-mapping diffs;
/// (3) the revision's item-mapping snapshot drives newly-resolved / newly-unresolved /
///     newly-flagged diffs (with live-table fallback when the snapshot is empty);
/// (4) skip matrix — missing source file and PDF (AI-extracted) sources skip-with-note,
///     download failures are recorded per order and never thrown;
/// (5) the test pack evidence JSON carries the <c>parseLeg</c> summary, with parse DIFFERENCES
///     informational and re-parse FAILURES gating honestly.
/// Uses the real <see cref="ProcuLinkDbContext"/> on the EF InMemory provider, the real CSV
/// transform + parser, and a mocked <see cref="IFileStorageService"/>.
/// </summary>
public class ReplayParseLegTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IEnumerable<ITransformService> Transformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    private static OrderParserFactory ParserFactory() =>
        new(new IPurchaseOrderParser[] { new CsvOrderParser() });

    /// <summary>A usable revision input-mapping snapshot for the ref/line/sku/qty/price CSV columns.</summary>
    private static string InputMappingJson() => JsonSerializer.Serialize(new PoMappingConfig
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

    private static Mock<IFileStorageService> StorageReturning(string csv)
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        return storage;
    }

    // ── seed helpers ───────────────────────────────────────────────────────────

    private static async Task<(Guid orgId, Guid supplierId, Guid connectionId, Guid revisionId)>
        SeedConnectionWithRevisionAsync(
            ProcuLinkDbContext db,
            string? inputMappingJson = null,
            params (string buyer, string supplier)[] itemMappings)
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme OÜ", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Acme OÜ",
            CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "draft", CreatedAt = now,
            OutputFormat = "csv", InputMappingJson = inputMappingJson,
            ItemMappings = itemMappings.Select(m => new ConnectionRevisionItemMapping
            {
                Id = Guid.NewGuid(), RevisionId = revisionId,
                BuyerItemCode = m.buyer, SupplierItemCode = m.supplier,
                Confidence = 1f, Source = "manual",
            }).ToList(),
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, revisionId);
    }

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string poNumber = "PO-OLD",
        string? sourceFileKey = null,
        params (int lineNo, string buyerCode, string? supplierCode, bool needsReview)[] lines)
    {
        var orderId = Guid.NewGuid();
        var lineSpecs = lines.Length > 0
            ? lines
            : new[] { (lineNo: 1, buyerCode: "WIDGET", supplierCode: (string?)"SUP-1", needsReview: false) };
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = poNumber,
            Currency = "EUR", OrderDate = new DateOnly(2026, 1, 1), Status = "ready",
            SourceFileKey = sourceFileKey, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = lineSpecs.Select(l => new PurchaseOrderLineEntity
            {
                Id = Guid.NewGuid(), OrderId = orderId, LineNumber = l.lineNo,
                BuyerItemCode = l.buyerCode, SupplierItemCode = l.supplierCode,
                Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 9.99m,
                NeedsReview = l.needsReview, Confidence = l.needsReview ? 0f : 1f,
            }).ToList(),
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    // ── 1. GOLDEN: parse leg off ⇒ response byte-identical, storage untouched ──

    [Fact]
    public async Task ParseLegOff_DefaultRequest_ResponseByteIdentical_AndStorageNeverTouched()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.csv");

        var storage = StorageReturning("ref,line,sku,qty,price\nPO-NEW,1,WIDGET,2,9.99\n");

        // Pre-flip-style construction (no parse-leg deps) vs. fully-wired construction.
        var legacy = new ReplayService(db, Transformers());
        var wired  = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var legacyResult = await legacy.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);
        var wiredResult  = await wired.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(legacyResult);
        Assert.NotNull(wiredResult);

        // Byte-identical serialized contract; no parseLeg field is ever emitted when off.
        var legacyJson = JsonSerializer.Serialize(legacyResult, CamelCase);
        var wiredJson  = JsonSerializer.Serialize(wiredResult, CamelCase);
        Assert.Equal(legacyJson, wiredJson);
        Assert.DoesNotContain("parseLeg", wiredJson);
        Assert.Null(Assert.Single(wiredResult!.Orders).ParseLeg);

        // The source file is never downloaded when the leg is off.
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 2. CSV re-parsed under a DIFFERENT revision input mapping ──────────────

    [Fact]
    public async Task ParseLeg_CsvUnderDifferentInputMapping_ShowsHeaderAndCodeDiff()
    {
        var db = MakeDb();
        // The revision maps PoNumber from the "ref" column and WIDGET → REV-W; the stored
        // canonical was parsed differently (PO-OLD, WIDGET → SUP-1).
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, InputMappingJson(), ("WIDGET", "REV-W"));
        await SeedOrderAsync(db, orgId, supplierId, poNumber: "PO-OLD",
            sourceFileKey: $"{orgId}/x/order.csv",
            (1, "WIDGET", "SUP-1", false));

        var storage = StorageReturning("ref,line,sku,qty,price\nPO-NEW,1,WIDGET,2,9.99\n");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg;
        Assert.NotNull(leg);
        Assert.Equal("reparsed", leg!.Status);
        Assert.True(leg.ParseChanged);

        // Header diff: the revision's input mapping reads the PO number from a different column.
        var poChange = Assert.Single(leg.HeaderChanges, c => c.Field == "PoNumber");
        Assert.Equal("PO-OLD", poChange.CurrentValue);
        Assert.Equal("PO-NEW", poChange.DraftValue);

        // Code-mapping diff: resolved on both sides but to a DIFFERENT supplier code.
        var codeChange = Assert.Single(leg.CodesResolvedDifferently);
        Assert.Equal(1, codeChange.LineNumber);
        Assert.Equal("WIDGET", codeChange.BuyerItemCode);
        Assert.Equal("SUP-1", codeChange.CurrentSupplierItemCode);
        Assert.Equal("REV-W", codeChange.ReParsedSupplierItemCode);

        Assert.False(leg.LineCountChanged);
        Assert.Equal(1, leg.CurrentLineCount);
        Assert.Equal(1, leg.ReParsedLineCount);
    }

    // ── 3. Item-mapping snapshot resolution diff (resolve / unresolve / flag flips) ──

    [Fact]
    public async Task ParseLeg_ItemSnapshotDiff_NewlyResolvedUnresolved_AndFlagFlips()
    {
        var db = MakeDb();
        // Snapshot maps WIDGET only. Currently: WIDGET is UNRESOLVED (flagged), GIZMO is
        // RESOLVED (clean) — the snapshot flips both.
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, InputMappingJson(), ("WIDGET", "REV-W"));
        await SeedOrderAsync(db, orgId, supplierId, poNumber: "PO-OLD",
            sourceFileKey: $"{orgId}/x/order.csv",
            (1, "WIDGET", (string?)null, true),
            (2, "GIZMO", "SUP-G", false));

        var storage = StorageReturning(
            "ref,line,sku,qty,price\nPO-OLD,1,WIDGET,2,9.99\nPO-OLD,2,GIZMO,2,9.99\n");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("reparsed", leg.Status);
        Assert.True(leg.ParseChanged);
        Assert.Empty(leg.HeaderChanges); // same PO number — only resolution changed

        var nowResolved = Assert.Single(leg.CodesNewlyResolved);
        Assert.Equal("WIDGET", nowResolved.BuyerItemCode);
        Assert.Null(nowResolved.CurrentSupplierItemCode);
        Assert.Equal("REV-W", nowResolved.ReParsedSupplierItemCode);

        var nowUnresolved = Assert.Single(leg.CodesNewlyUnresolved);
        Assert.Equal("GIZMO", nowUnresolved.BuyerItemCode);
        Assert.Equal("SUP-G", nowUnresolved.CurrentSupplierItemCode);
        Assert.Null(nowUnresolved.ReParsedSupplierItemCode);

        Assert.Equal(new[] { 2 }, leg.LinesNewlyFlagged);   // GIZMO would now need review
        Assert.Equal(new[] { 1 }, leg.LinesNewlyUnflagged); // WIDGET would now be clean
    }

    [Fact]
    public async Task ParseLeg_EmptyItemSnapshot_FallsBackToLiveItemMappings()
    {
        var db = MakeDb();
        // Revision snapshotted NO item mappings → the runtime contract (and so the leg) falls
        // back to the LIVE item_mappings table, which maps WIDGET → LIVE-W.
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "WIDGET", SupplierItemCode = "LIVE-W", Confidence = 1f,
            Source = "manual", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, orgId, supplierId, poNumber: "PO-OLD",
            sourceFileKey: $"{orgId}/x/order.csv",
            (1, "WIDGET", (string?)null, true));

        var storage = StorageReturning("ref,line,sku,qty,price\nPO-OLD,1,WIDGET,2,9.99\n");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("reparsed", leg.Status);
        var nowResolved = Assert.Single(leg.CodesNewlyResolved);
        Assert.Equal("LIVE-W", nowResolved.ReParsedSupplierItemCode);
    }

    // ── 4. Skip matrix + defensive failures ────────────────────────────────────

    [Fact]
    public async Task ParseLeg_MissingSourceFile_SkippedWithNote_NoDownload()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: null); // API-ingress style order

        var storage = StorageReturning("irrelevant");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("skipped", leg.Status);
        Assert.Contains("No stored source file", leg.Note);
        Assert.False(leg.ParseChanged);
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ParseLeg_PdfSource_SkippedWithAiNote_NoDownload()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.pdf");

        var storage = StorageReturning("irrelevant");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("skipped", leg.Status);
        Assert.Equal("AI-extracted source, parse leg skipped.", leg.Note);
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ParseLeg_DownloadFailure_RecordedPerOrder_NeverThrown()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.csv");

        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("R2 unavailable"));
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        // The replay itself still succeeds — the failure is a per-order note.
        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.CurrentOutput); // output leg unaffected
        Assert.Equal("failed", diff.ParseLeg!.Status);
        Assert.Contains("R2 unavailable", diff.ParseLeg.Note);
    }

    [Fact]
    public async Task ParseLeg_UnparseableSource_RecordedPerOrder_NeverThrown()
    {
        var db = MakeDb();
        // No usable revision input mapping AND no live PO mapping → parser-factory routing;
        // an .xlsx source with garbage bytes fails inside the parser, recorded per order.
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, inputMappingJson: null);
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.xlsx");

        var storage = StorageReturning("this is not a real xlsx");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory()); // CSV-only factory → .xlsx unsupported

        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("failed", leg.Status);
        Assert.Contains("Re-parse failed", leg.Note);
    }

    [Fact]
    public async Task ParseLeg_NoStorageWiredOnHost_SkippedWithNote()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(db, InputMappingJson());
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.csv");

        // Legacy construction without parse-leg deps: the leg degrades to skip, never throws.
        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        var leg = Assert.Single(result!.Orders).ParseLeg!;
        Assert.Equal("skipped", leg.Status);
        Assert.Contains("not available", leg.Note);
    }

    // ── 5. Replay stays non-mutating with the leg on ───────────────────────────

    [Fact]
    public async Task ParseLeg_On_WritesNothing()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithRevisionAsync(
            db, InputMappingJson(), ("WIDGET", "REV-W"));
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.csv",
            lines: new[] { (1, "WIDGET", (string?)null, true) });

        var ordersBefore = db.PurchaseOrders.Count();
        var linesBefore  = db.PurchaseOrderLines.Count();
        var auditBefore  = db.AuditEvents.Count();

        var storage = StorageReturning("ref,line,sku,qty,price\nPO-NEW,1,WIDGET,2,9.99\n");
        var svc = new ReplayService(db, Transformers(),
            fileStorage: storage.Object, parserFactory: ParserFactory());
        var result = await svc.ReplayAsync(orgId, connId, revId,
            new ReplayRequest(IncludeParseLeg: true), CancellationToken.None);

        Assert.NotNull(Assert.Single(result!.Orders).ParseLeg);
        var dirty = db.ChangeTracker.Entries()
            .Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached)
            .ToList();
        Assert.Empty(dirty);
        Assert.Equal(ordersBefore, db.PurchaseOrders.Count());
        Assert.Equal(linesBefore,  db.PurchaseOrderLines.Count());
        Assert.Equal(auditBefore,  db.AuditEvents.Count());

        // The stored line is untouched: still unresolved/flagged despite the leg resolving it in memory.
        var line = db.PurchaseOrderLines.AsNoTracking().Single();
        Assert.Null(line.SupplierItemCode);
        Assert.True(line.NeedsReview);
    }

    // ── 6. Test-pack evidence carries the parseLeg summary ─────────────────────

    private static SupplierConnectionService TestPackService(
        ProcuLinkDbContext db, IFileStorageService storage) =>
        new(db,
            new ReplayService(db, Transformers(),
                fileStorage: storage, parserFactory: ParserFactory()),
            new ProcuLink.Transform.Conformance.ConformanceService());

    private static ConnectionRevisionDraftInput DraftBundle(string inputMappingJson) => new(
        InputMappingJson: inputMappingJson,
        OutputMappingJson: null,
        OutputFormat: "csv",
        DeliveryProtocol: "http",
        DeliveryConfigJson: "{\"url\":\"https://example.test\"}",
        DeliveryAutoDeliver: false,
        CredentialsRef: null,
        AcceptanceProfileId: null,
        AcceptanceVersionNo: null,
        CatalogMode: "live",
        ItemMappings: new List<ConnectionItemMappingInput> { new("WIDGET", "REV-W", 1.0f, "manual") });

    [Fact]
    public async Task TestPack_EvidenceCarriesParseLeg_DifferencesInformationalNotFailures()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Evidence OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        // Stored canonical: PO-OLD / WIDGET → SUP-1. The CSV source re-parses to PO-NEW / REV-W
        // under the draft's input + item snapshots → a parse DIFFERENCE, which must be
        // informational: the pack still passes.
        await SeedOrderAsync(db, orgId, supplierId, poNumber: "PO-OLD",
            sourceFileKey: $"{orgId}/x/order.csv",
            (1, "WIDGET", "SUP-1", false));

        var storage = StorageReturning("ref,line,sku,qty,price\nPO-NEW,1,WIDGET,2,9.99\n");
        var svc = TestPackService(db, storage.Object);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, DraftBundle(InputMappingJson()), false, "u", CancellationToken.None);

        var evidence = await svc.RunTestPackAsync(orgId, conn.Id, draft!.Id, CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Passed); // parse differences are informational, not failures
        Assert.Contains("\"parseLeg\":", evidence.SummaryJson);
        Assert.Contains("\"ordersReParsed\":1", evidence.SummaryJson);
        Assert.Contains("\"parseChanges\":1", evidence.SummaryJson);
        Assert.Contains("\"failures\":0", evidence.SummaryJson);
    }

    [Fact]
    public async Task TestPack_AllSourceFilesFailToReParse_FailsHonestly()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Evidence OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: $"{orgId}/x/order.csv");

        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("R2 unavailable"));

        var svc = TestPackService(db, storage.Object);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, DraftBundle(InputMappingJson()), false, "u", CancellationToken.None);

        var evidence = await svc.RunTestPackAsync(orgId, conn.Id, draft!.Id, CancellationToken.None);

        // Source files exist but NONE re-parsed ⇒ the parse leg gates publish honestly.
        Assert.NotNull(evidence);
        Assert.False(evidence!.Passed);
        Assert.Contains("\"failures\":1", evidence.SummaryJson);
        Assert.Contains("\"ordersReParsed\":0", evidence.SummaryJson);
    }

    [Fact]
    public async Task TestPack_NoSourceFiles_ParseLegSkipsWithNote_StillPasses()
    {
        var db = MakeDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Evidence OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, orgId, supplierId, sourceFileKey: null); // API-ingress order

        var storage = StorageReturning("irrelevant");
        var svc = TestPackService(db, storage.Object);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, DraftBundle(InputMappingJson()), false, "u", CancellationToken.None);

        var evidence = await svc.RunTestPackAsync(orgId, conn.Id, draft!.Id, CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Passed);
        Assert.Contains("\"parseLeg\":", evidence.SummaryJson);
        Assert.Contains("\"skipped\":1", evidence.SummaryJson);
        Assert.Contains("\"ordersReParsed\":0", evidence.SummaryJson);
    }
}
