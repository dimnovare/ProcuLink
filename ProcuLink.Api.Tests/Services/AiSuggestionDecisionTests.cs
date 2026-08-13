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
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Durable AI-suggestion decision history (auto/be-ai-history): verifies that resolving or
/// bulk-accepting a line writes a permanent decision record, that records are org-scoped, and
/// that the write is idempotent across retries. The existing resolve/accept behaviour is
/// asserted to be unchanged alongside.
/// </summary>
public class AiSuggestionDecisionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
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
        itemMappings
            .Setup(s => s.UpsertAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<MappingSource>(), It.IsAny<float?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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

        var fileStorage = new Mock<IFileStorageService>();

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
            new OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedOrderWithLinesAsync(
        IEnumerable<PurchaseOrderLineEntity> lines, Guid? orgId = null)
    {
        var db      = NewDb();
        var org     = orgId ?? Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = org,
            Name      = "Test Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        var order = new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = org,
            SupplierId = supplierId,
            PoNumber   = "PO-TEST",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = OrderStatusConstants.PendingReview,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines      = lines.ToList(),
        };

        foreach (var l in order.Lines) l.OrderId = orderId;

        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return (db, org, orderId);
    }

    private static PurchaseOrderLineEntity LineWithSuggestion(
        int lineNumber, string buyerCode, string suggested, float confidence) => new()
    {
        Id                          = Guid.NewGuid(),
        LineNumber                  = lineNumber,
        BuyerItemCode               = buyerCode,
        NeedsReview                 = true,
        AiSuggestedSupplierItemCode = suggested,
        AiSuggestionConfidence      = confidence,
        AiSuggestionReason          = "Best match",
        AiSuggestionProvenance      = "catalog",
        Confidence                  = 0.0f,
    };

    // ── Accept (bulk-accept) records an "accepted" decision ───────────────────

    [Fact]
    public async Task AcceptAiSuggestions_RecordsAcceptedDecision()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.92f),
        });
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(orgId, orderId, 0.85, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value); // existing behaviour unchanged

        var history = await new AiSuggestionDecisionService(db).ListForOrderAsync(orgId, orderId, CancellationToken.None);

        var d = Assert.Single(history);
        Assert.Equal(AiSuggestionDecisionKind.Accepted, d.Decision);
        Assert.Equal(1, d.LineNumber);
        Assert.Equal("SUP-111", d.SuggestedSupplierItemCode);
        Assert.Equal("SUP-111", d.ChosenSupplierItemCode);
        Assert.Equal(0.92, d.Confidence!.Value, 3);
        Assert.Equal("ai", d.DecidedBy);
        Assert.NotNull(d.CandidateSetJson);
    }

    // ── Resolve keeping the suggested code records "accepted" ─────────────────

    [Fact]
    public async Task Resolve_WithSuggestedCode_RecordsAcceptedDecision()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.80f),
        });
        var svc = BuildService(db);

        // User confirms the AI's suggested code.
        var resolutions = new List<LineResolution> { new(1, "SUP-111") };
        var result = await svc.ResolveAsync(orgId, orderId, resolutions, saveMappings: false, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var history = await new AiSuggestionDecisionService(db).ListForOrderAsync(orgId, orderId, CancellationToken.None);
        var d = Assert.Single(history);
        Assert.Equal(AiSuggestionDecisionKind.Accepted, d.Decision);
        Assert.Equal("SUP-111", d.ChosenSupplierItemCode);
        Assert.Equal("user", d.DecidedBy);
    }

    // ── Resolve choosing a DIFFERENT code records "rejected" ──────────────────

    [Fact]
    public async Task Resolve_WithDifferentCode_RecordsRejectedDecision()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.55f),
        });
        var svc = BuildService(db);

        // User overrides the AI suggestion with a different code.
        var resolutions = new List<LineResolution> { new(1, "SUP-999") };
        var result = await svc.ResolveAsync(orgId, orderId, resolutions, saveMappings: false, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var history = await new AiSuggestionDecisionService(db).ListForOrderAsync(orgId, orderId, CancellationToken.None);
        var d = Assert.Single(history);
        Assert.Equal(AiSuggestionDecisionKind.Rejected, d.Decision);
        Assert.Equal("SUP-111", d.SuggestedSupplierItemCode);
        Assert.Equal("SUP-999", d.ChosenSupplierItemCode);
        Assert.Equal(0.55, d.Confidence!.Value, 3);
    }

    // ── Resolve with no AI suggestion records "manual" ────────────────────────

    [Fact]
    public async Task Resolve_NoSuggestion_RecordsManualDecision()
    {
        var line = new PurchaseOrderLineEntity
        {
            Id            = Guid.NewGuid(),
            LineNumber    = 1,
            BuyerItemCode = "BC-001",
            NeedsReview   = true,
            Confidence    = 0.0f,
            // no AI suggestion fields set
        };
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[] { line });
        var svc = BuildService(db);

        var resolutions = new List<LineResolution> { new(1, "SUP-MANUAL") };
        var result = await svc.ResolveAsync(orgId, orderId, resolutions, saveMappings: false, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var history = await new AiSuggestionDecisionService(db).ListForOrderAsync(orgId, orderId, CancellationToken.None);
        var d = Assert.Single(history);
        Assert.Equal(AiSuggestionDecisionKind.Manual, d.Decision);
        Assert.Equal(string.Empty, d.SuggestedSupplierItemCode);
        Assert.Equal("SUP-MANUAL", d.ChosenSupplierItemCode);
        Assert.Null(d.Confidence);
    }

    // ── Records are org-scoped — another org cannot read them ─────────────────

    [Fact]
    public async Task ListForOrder_IsOrgScoped()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.92f),
        });
        var svc = BuildService(db);
        await svc.AcceptAiSuggestionsAsync(orgId, orderId, 0.85, CancellationToken.None);

        var service = new AiSuggestionDecisionService(db);

        // Correct org sees the row.
        Assert.Single(await service.ListForOrderAsync(orgId, orderId, CancellationToken.None));

        // A different org sees nothing for the same order id.
        var otherOrg = Guid.NewGuid();
        Assert.Empty(await service.ListForOrderAsync(otherOrg, orderId, CancellationToken.None));
    }

    // ── Idempotent across retries — re-recording the same decision updates, not duplicates ──

    [Fact]
    public async Task RecordMany_IsIdempotent_AcrossRetries()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.92f),
        });
        var service = new AiSuggestionDecisionService(db);

        var record = new AiSuggestionDecisionRecord(
            LineNumber:                1,
            SuggestedSupplierItemCode: "SUP-111",
            ChosenSupplierItemCode:    "SUP-111",
            CandidateSetJson:          null,
            Confidence:                0.92,
            ModelVersion:              "gpt-5-mini",
            Decision:                  AiSuggestionDecisionKind.Accepted,
            DecidedBy:                 "user");

        // Simulate a Hangfire retry / double-submit: record the identical decision twice.
        await service.RecordAsync(orgId, orderId, record, CancellationToken.None);
        await service.RecordAsync(orgId, orderId, record, CancellationToken.None);

        var history = await service.ListForOrderAsync(orgId, orderId, CancellationToken.None);
        Assert.Single(history); // exactly one row, not two
    }

    // ── Existing resolve flow is unchanged: lines resolved, Ai* fields cleared ──

    [Fact]
    public async Task Resolve_StillClearsAiFields_AndResolvesLine()
    {
        var (db, orgId, orderId) = await SeedOrderWithLinesAsync(new[]
        {
            LineWithSuggestion(1, "BC-001", "SUP-111", 0.80f),
        });
        var svc = BuildService(db);

        var resolutions = new List<LineResolution> { new(1, "SUP-111") };
        var result = await svc.ResolveAsync(orgId, orderId, resolutions, saveMappings: false, CancellationToken.None);
        Assert.True(result.IsSuccess);

        var order = await db.PurchaseOrders.Include(o => o.Lines).FirstAsync(o => o.Id == orderId);
        var line  = order.Lines.First(l => l.LineNumber == 1);

        Assert.Equal(OrderStatusConstants.Ready, order.Status);
        Assert.False(line.NeedsReview);
        Assert.Equal("SUP-111", line.SupplierItemCode);
        Assert.Null(line.AiSuggestedSupplierItemCode);
        Assert.Null(line.AiSuggestionConfidence);
        Assert.Null(line.AiSuggestionReason);
        Assert.Null(line.AiSuggestionProvenance);
    }
}
