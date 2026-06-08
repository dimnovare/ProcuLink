using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Verifies that a CSV line whose quantity / unit price the parser could not read
/// unambiguously (e.g. scientific notation "1.5e2") is persisted with
/// <c>NeedsReview = true</c> — even when its supplier item code resolves cleanly —
/// so the silently-wrong value surfaces in /operations/exceptions instead of being
/// delivered. This pins the ingestion-layer plumb of
/// <see cref="ParsedOrderLine.NeedsReview"/> through <c>BuildLineEntitiesAsync</c>.
///
/// The test resolves BOTH lines' codes so the ONLY remaining source of review is the
/// parser flag — isolating it from the ordinary "unresolved mapping" review path.
/// Uses the synchronous <c>CreateFromFileAsync</c> path because the async
/// <c>ParseStoredFileAsync</c> success branch uses ExecuteUpdateAsync, which the EF
/// InMemory provider cannot translate.
/// </summary>
public class OrderServiceCsvReviewFlagTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
    {
        var parserFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
        });

        // Both buyer codes resolve → the only review source left is the parser flag.
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUY-1"] = "SUP-1",
                ["BUY-2"] = "SUP-2",
            });

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string key, string _, CancellationToken _) => key);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage.Object,
            parserFactory,
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    [Fact]
    public async Task CreateFromFileAsync_ScientificNotationPrice_ResolvedLine_StillNeedsReview()
    {
        var db  = NewDb();
        var svc = BuildService(db);

        var csv =
            "ponumber,orderdate,currency,buyername,linenumber,buyeritemcode,description,quantity,unit,unitprice" + "\n" +
            "PO-SCI,2026-06-08,EUR,Buyer,1,BUY-1,Clean line,10,EA,12.50" + "\n" +
            "PO-SCI,2026-06-08,EUR,Buyer,2,BUY-2,Sci notation,1,EA,1.5e2" + "\n";

        var result = await svc.CreateFromFileAsync(
            Guid.NewGuid(), Guid.NewGuid(),
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), "order.csv", "text/csv", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var order = result.Value!;
        order.Status.Should().Be("pending_review", "the ambiguous-price line forces the order to review");

        var clean = order.Lines.Single(l => l.LineNumber == 1);
        clean.SupplierItemCode.Should().Be("SUP-1");
        clean.NeedsReview.Should().BeFalse("a clean, resolved line needs no review");
        clean.UnitPrice.Should().Be(12.50m);

        var sci = order.Lines.Single(l => l.LineNumber == 2);
        sci.SupplierItemCode.Should().Be("SUP-2", "the code resolved deterministically");
        sci.NeedsReview.Should().BeTrue("a scientific-notation price must force review even when the code resolved");
        sci.Confidence.Should().BeLessThanOrEqualTo(0.5f, "a parser-flagged line's confidence is capped");
        sci.UnitPrice.Should().NotBe(1.52m, "the silently-wrong 1.52 must never be persisted");
        sci.UnitPrice.Should().Be(0m, "an unparseable price degrades to 0 (visible), not a guessed value");
    }
}
