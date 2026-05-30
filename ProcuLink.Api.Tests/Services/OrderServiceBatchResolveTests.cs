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
/// Verifies the perf/parse-loop-batching change: <see cref="OrderService"/> resolves
/// every line via the batched <c>ResolveManyAsync</c> + a single
/// <c>SuggestSupplierItemCodesAsync</c> call, and NEVER falls back to the per-line
/// <c>ResolveAsync</c> / <c>SuggestSupplierItemCodeAsync</c> methods. Per-line field +
/// NeedsReview semantics are unchanged.
///
/// Driven through <c>CreateFromFileAsync</c> (which persists via the change tracker)
/// rather than <c>ParseStoredFileAsync</c> (which uses <c>ExecuteUpdateAsync</c>, not
/// translatable by the EF InMemory provider). Both share the same batched
/// <c>BuildLineEntitiesAsync</c> code path.
/// </summary>
public class OrderServiceBatchResolveTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedSupplierAsync()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Batch Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
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
            poMappings.Object,
            aiMappings,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    // Two-line CSV (no explicit lineNumber column → auto-numbered 1, 2).
    private const string TwoLineCsv =
        "itemcode,description,quantity,price\n" +
        "BUYER-1,Widget,2,9.99\n" +
        "BUYER-2,Gadget,3,4.50\n";

    [Fact]
    public async Task CreateFromFileAsync_UsesBatchResolveAndBatchAi_NotPerLineMethods()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        // BUYER-1 resolves deterministically; BUYER-2 does not and gets an AI suggestion.
        var itemMappings = new Mock<IItemMappingService>(MockBehavior.Strict);
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                orgId, supplierId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUYER-1"] = "SUP-1",   // resolved
                ["BUYER-2"] = null,      // unresolved → eligible for AI
            });

        var aiMappings = new Mock<IAiMappingService>(MockBehavior.Strict);
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                orgId, supplierId, "Batch Supplier",
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>
            {
                [2] = new AiMappingSuggestion("AI-SUP-2", 0.77f, "looks like a match", "buyer code"),
            });

        var svc = Build(db, FileStorageMock().Object, itemMappings.Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TwoLineCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var orderId = result.Value!.Id;

        var lines = await db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync();

        Assert.Equal(2, lines.Count);

        // Line 1 — deterministically resolved.
        var line1 = lines[0];
        Assert.Equal("SUP-1", line1.SupplierItemCode);
        Assert.False(line1.NeedsReview);
        Assert.Null(line1.AiSuggestedSupplierItemCode);

        // Line 2 — unresolved, carries the batched AI suggestion (not auto-applied).
        var line2 = lines[1];
        Assert.Null(line2.SupplierItemCode);
        Assert.True(line2.NeedsReview);
        Assert.Equal("AI-SUP-2", line2.AiSuggestedSupplierItemCode);
        Assert.Equal(0.77f, line2.AiSuggestionConfidence);
        Assert.Equal("looks like a match", line2.AiSuggestionReason);
        Assert.Equal("buyer code", line2.AiSuggestionProvenance);

        // Order is pending review because a line still needs review.
        Assert.Equal("pending_review", result.Value!.Status);

        // The batched methods were each used exactly once.
        itemMappings.Verify(s => s.ResolveManyAsync(
            orgId, supplierId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        aiMappings.Verify(s => s.SuggestSupplierItemCodesAsync(
            orgId, supplierId, "Batch Supplier",
            It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
            It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Strict mocks would already throw on a per-line call; assert intent explicitly.
        itemMappings.Verify(s => s.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        aiMappings.Verify(s => s.SuggestSupplierItemCodeAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<AiMappingLineContext>(),
            It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateFromFileAsync_OnlyUnresolvedLinesSentToAi()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUYER-1"] = "SUP-1",   // resolved — must NOT be sent to AI
                ["BUYER-2"] = null,      // unresolved — the only line AI should see
            });

        IReadOnlyList<AiMappingLineContext>? captured = null;
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, IReadOnlyList<AiMappingLineContext>, IReadOnlyList<AiMappingCandidate>, CancellationToken>(
                (_, _, _, lines, _, _) => captured = lines)
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var svc = Build(db, FileStorageMock().Object, itemMappings.Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TwoLineCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.NotNull(captured);
        var only = Assert.Single(captured!);
        Assert.Equal(2, only.LineNumber);
        Assert.Equal("BUYER-2", only.BuyerItemCode);
    }

    [Fact]
    public async Task CreateFromFileAsync_AllLinesResolved_NeverCallsAi_AndOrderIsReady()
    {
        var (db, orgId, supplierId) = await SeedSupplierAsync();

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["BUYER-1"] = "SUP-1",
                ["BUYER-2"] = "SUP-2",
            });

        // No AI setup: if it were called Moq returns null → service would NPE. The
        // Times.Never assertion is the real guarantee.
        var aiMappings = new Mock<IAiMappingService>();

        var svc = Build(db, FileStorageMock().Object, itemMappings.Object, aiMappings.Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(TwoLineCsv));
        var result = await svc.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("ready", result.Value!.Status);
        Assert.All(result.Value!.Lines, l => Assert.False(l.NeedsReview));

        aiMappings.Verify(s => s.SuggestSupplierItemCodesAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
            It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
