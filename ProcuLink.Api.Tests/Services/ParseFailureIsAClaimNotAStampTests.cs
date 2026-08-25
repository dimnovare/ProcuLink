using System.Text;
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

/// <summary>
/// The parse-failure writer must take an ATOMIC CLAIM, not stamp terminal <c>failed</c> over
/// whatever it finds.
///
/// <para><b>The defect.</b> <c>OrderIngestionService.ParseStoredFileAsync</c> reads the order once,
/// at the top, and refuses unless the status is <c>parsing</c>. Every failure write below that read
/// lands much later — after a storage download, a format detection, and on the PDF/XLSX paths a
/// network call to an LLM extractor — and the row is unclaimed for that whole window.
/// <c>SetOrderFailedAsync</c> used to re-load the row and write <c>failed</c> onto it with no
/// from-status test at all. Its comment even said "the row is re-loaded at the moment of failure,
/// so it is not stale", which mistakes a FRESH read for OWNERSHIP: re-reading tells you what the
/// status is, and then you overwrite it anyway.</para>
///
/// <para><b>Why the two racers below are ordinary flows, not exotic ones.</b>
/// <c>StuckOrderDetectionService</c> re-drives a stalled parse by KEEPING the order in
/// <c>parsing</c> and enqueuing a fresh job, so two live parse jobs for one order is the recovery
/// path working as designed — and the slow one finishing second overwrote the fast one's success.
/// <c>OrderResolutionService.MarkRejectedAsync</c> has no from-status guard beyond "not finished",
/// and <c>parsing → rejected_by_supplier</c> is a documented edge, so an operator can record a
/// supplier's refusal mid-parse and have their finding replaced by "something went wrong".</para>
///
/// <para><b>Why it was unrecoverable rather than merely wrong.</b> <c>failed</c> is the sole member
/// of <see cref="OrderStatusMachine.DeclaredTerminal"/>, so <c>OrderResolutionService.IsFinished</c>
/// then refuses resolve, accept-AI and mark-rejected alike. The order is WEDGED in a terminal lie
/// whose only cure is a new order row or a database edit — which is why each test below asserts the
/// order is still OUT of DeclaredTerminal, not merely that the status string differs.</para>
///
/// <para>The concurrent writer is injected through the file-storage download, which is the real
/// window: it runs after the top-of-method status read and before every failure site.</para>
/// </summary>
public class ParseFailureIsAClaimNotAStampTests
{
    /// <summary>
    /// The anti-vacuity control. Same file, same harness, NO concurrent writer — the claim wins and
    /// everything is recorded exactly as before. Without this the three tests below would pass just
    /// as happily against a parse that never wrote anything at all.
    /// </summary>
    [Fact]
    public async Task ParseFailure_WithNoConcurrentWriter_StillFailsTheOrderAndRecordsIt()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync();

        var svc    = BuildService(db, EmptyCsvStorage(db, orderId, flipTo: null));
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.Failed, order.Status);
        Assert.Equal(1, await CountParseFailedAsync(db, orderId));
    }

    /// <summary>
    /// A concurrent parse SUCCEEDED while this attempt was downloading. Its result must stand.
    /// </summary>
    [Fact]
    public async Task ParseFailure_LosingToAConcurrentSuccessfulParse_LeavesTheSuccessStanding()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync();

        var svc = BuildService(db,
            EmptyCsvStorage(db, orderId, flipTo: OrderStatusConstants.PendingReview));
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.PendingReview, order.Status);
        Assert.DoesNotContain(order.Status, OrderStatusMachine.DeclaredTerminal);

        // Nothing recorded: "parse failed" is not what happened to this order, and reconciling
        // exceptions off a failure that was never committed would raise one for a healthy order.
        Assert.Equal(0, await CountParseFailedAsync(db, orderId));
    }

    /// <summary>
    /// An operator recorded the supplier's refusal while this attempt was in flight. A human's
    /// verdict outranks a generic machine failure — and <c>rejected_by_supplier</c> is load-bearing
    /// rather than decorative: it feeds the supplier acceptance-rate figures the dashboard reports.
    /// </summary>
    [Fact]
    public async Task ParseFailure_LosingToAnOperatorRejection_LeavesTheVerdictStanding()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync();

        var svc = BuildService(db,
            EmptyCsvStorage(db, orderId, flipTo: OrderStatusConstants.RejectedBySupplier));
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.RejectedBySupplier, order.Status);
        Assert.DoesNotContain(order.Status, OrderStatusMachine.DeclaredTerminal);
        Assert.Equal(0, await CountParseFailedAsync(db, orderId));
    }

    /// <summary>
    /// The status the claim is keyed on is the machine's, not a literal — so widening
    /// <see cref="OrderStatusMachine.ParseFailableFrom"/> tomorrow changes this behaviour
    /// deliberately, in one place, instead of by transcription at the call site.
    /// </summary>
    [Fact]
    public void ParseClaim_ReadsItsAdmissibleStatusesFromTheMachine()
        => Assert.Equal(new[] { OrderStatusConstants.Parsing },
                        OrderStatusMachine.ParseFailableFrom.OrderBy(s => s, StringComparer.Ordinal).ToArray());

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A header-only CSV (zero data rows) drives the "0 lines parsed" failure site. When
    /// <paramref name="flipTo"/> is non-null, the download ALSO commits a competing status write —
    /// which is the real race window: it happens after ParseStoredFileAsync's top-of-method status
    /// read and before every failure site below it.
    /// </summary>
    private static IFileStorageService EmptyCsvStorage(ProcuLinkDbContext db, Guid orderId, string? flipTo)
    {
        var csvBytes = Encoding.UTF8.GetBytes("foo,bar,baz\n");

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (flipTo is not null)
                {
                    var racer = db.PurchaseOrders.Single(o => o.Id == orderId);
                    racer.Status    = flipTo;
                    racer.UpdatedAt = DateTime.UtcNow;
                    db.SaveChanges();
                }

                return new MemoryStream(csvBytes);
            });

        return fileStorage.Object;
    }

    private static Task<int> CountParseFailedAsync(ProcuLinkDbContext db, Guid orderId) =>
        db.AuditEvents.AsNoTracking()
          .CountAsync(e => e.EntityId == orderId && e.Action == "ParseFailed");

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedParsingOrderAsync()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Test Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-RACE",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = OrderStatusConstants.Parsing,
            SourceFileKey = $"{orgId}/{orderId}/file.csv",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (db, orgId, orderId);
    }

    private static OrderService BuildService(ProcuLinkDbContext db, IFileStorageService fileStorage)
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
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AiMappingLineContext>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiMappingSuggestion?)null);
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage,
            parserFactory,
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            structuredExtractor: null);
    }
}
