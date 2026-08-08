using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The parse-path hook: WHEN supplier auto-detect runs, and what happens when it does not work.
///
/// <para>Exercised through the sync <c>CreateStubFromParsedOrderAsync</c> entry point rather than
/// <c>ParseStoredFileAsync</c>, because the latter's persist block is a Postgres-only
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> transaction that InMemory cannot run. That path is
/// the one BE-1 created — a prose-only order parked <c>unrouted</c>, which never reaches
/// <c>ParseStoredFileAsync</c> at all — so it needs its own coverage regardless.</para>
/// </summary>
public class OrderIngestionSupplierSuggestionHookTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A scorer that always fails — stands in for any fault inside the real one.</summary>
    private sealed class ThrowingSuggestionService : ISupplierSuggestionService
    {
        public Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(SupplierSuggestionInput input, CancellationToken ct) =>
            throw new InvalidOperationException("scorer exploded");

        public Task RecordAsync(Guid organisationId, Guid orderId, IReadOnlyList<SupplierSuggestion> suggestions, CancellationToken ct) =>
            throw new InvalidOperationException("recorder exploded");
    }

    /// <summary>Records whether it was called at all — used to prove a routed order pays nothing.</summary>
    private sealed class CountingSuggestionService : ISupplierSuggestionService
    {
        public int SuggestCalls { get; private set; }
        public SupplierSuggestionInput? LastInput { get; private set; }
        public IReadOnlyList<SupplierSuggestion> Returns { get; set; } = Array.Empty<SupplierSuggestion>();

        private readonly ISupplierSuggestionService? _inner;
        public CountingSuggestionService(ISupplierSuggestionService? inner = null) => _inner = inner;

        public Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(SupplierSuggestionInput input, CancellationToken ct)
        {
            SuggestCalls++;
            LastInput = input;
            return Task.FromResult(Returns);
        }

        public Task RecordAsync(Guid organisationId, Guid orderId, IReadOnlyList<SupplierSuggestion> suggestions, CancellationToken ct) =>
            _inner?.RecordAsync(organisationId, orderId, suggestions, ct) ?? Task.CompletedTask;
    }

    private static OrderIngestionService BuildIngestion(
        ProcuLinkDbContext db, ISupplierSuggestionService? suggestions)
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var shared = new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance);

        return new OrderIngestionService(
            db,
            new Mock<IFileStorageService>().Object,
            new OrderParserFactory(Array.Empty<IPurchaseOrderParser>()),
            itemMappings.Object,
            new Mock<IPoMappingService>().Object,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            structuredExtractor: null,
            shared,
            catalogRetrieval: null,
            effectiveConfig: null,
            productCodeSearch: null,
            aiUsage: null,
            supplierSuggestions: suggestions);
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Org", Slug = $"org-{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static ExtractedOrder BodyOrder() => new(
        PoNumber:  "PO-PROSE-1",
        OrderDate: null,
        BuyerName: "Buyer Ltd",
        Currency:  "EUR",
        Lines: new List<ExtractedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "WIDGET-A", Description: "Widget A", Quantity: 2m,
                Unit: "EA", UnitPrice: 5m),
        },
        SupplierName: "Acme GmbH");

    // ── When the scorer runs ──────────────────────────────────────────────────

    [Fact]
    public async Task SupplierLessOrder_isScored_andItsSuggestionsArePersisted()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);
        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme GmbH", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var real = new SupplierSuggestionService(
            db, new SchemaFingerprintService(db, NullLogger<SchemaFingerprintService>.Instance),
            NullLogger<SupplierSuggestionService>.Instance);
        var scorer = new CountingSuggestionService(real)
        {
            Returns = new[]
            {
                new SupplierSuggestion(supplier.Id, "Acme GmbH", 1, 0.55, "because",
                    new[] { new SupplierSignalContribution(SupplierSignalKind.Name, 0.2, "name matches") }),
            },
        };

        var result = await BuildIngestion(db, scorer).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None,
            inboundSenderDomain: "acme.example");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, scorer.SuggestCalls);

        var row = await db.OrderSupplierSuggestions.AsNoTracking().SingleAsync();
        Assert.Equal(result.Value!.Id, row.OrderId);
        Assert.Equal(supplier.Id, row.SupplierId);
        Assert.Null(row.Decision);

        // The order is still parked for a human. A suggestion is never a routing decision.
        Assert.Equal(OrderStatusConstants.Unrouted, result.Value!.Status);
        Assert.Null(result.Value!.SupplierId);

        Assert.Contains(await db.AuditEvents.AsNoTracking().ToListAsync(),
            e => e.Action == "SupplierSuggested" && e.EntityId == result.Value!.Id);
    }

    [Fact]
    public async Task TheScorerIsGivenTheDocumentsEvidence_includingTheSenderDomain()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);
        var scorer = new CountingSuggestionService();

        await BuildIngestion(db, scorer).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None,
            inboundSenderDomain: "acme.example");

        var input = Assert.IsType<SupplierSuggestionInput>(scorer.LastInput);
        Assert.Equal(orgId, input.OrganisationId);
        Assert.Equal("Acme GmbH", input.DocumentSupplierName);
        Assert.Equal("acme.example", input.InboundSenderDomain);
        Assert.Equal(new[] { "WIDGET-A" }, input.LineCodes);
    }

    [Fact]
    public async Task RoutedOrder_isNeverScored()
    {
        // A routed order has a supplier already; running the scorer for it would be pure cost.
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);
        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Known Ltd", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var scorer = new CountingSuggestionService();

        var result = await BuildIngestion(db, scorer).CreateStubFromParsedOrderAsync(
            orgId, supplier.Id, BodyOrder(), "email_body_nlp", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, scorer.SuggestCalls);
        Assert.Empty(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NoScorerWired_stillCreatesTheOrder()
    {
        // The dependency is optional; without it the behaviour is exactly what it was before this
        // feature existed.
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);

        var result = await BuildIngestion(db, suggestions: null).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(OrderStatusConstants.Unrouted, result.Value!.Status);
        Assert.Empty(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());
    }

    // ── When the scorer fails ─────────────────────────────────────────────────

    [Fact]
    public async Task AThrowingScorer_doesNotFailTheOrder()
    {
        // A suggestion is a convenience; the order is the product. A scorer fault must never turn
        // a document we successfully read into a lost order.
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);

        var result = await BuildIngestion(db, new ThrowingSuggestionService()).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None,
            inboundSenderDomain: "acme.example");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(OrderStatusConstants.Unrouted, result.Value!.Status);
        Assert.Single(await db.PurchaseOrderLines.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AThrowingRecorder_doesNotFailTheOrder_andLeavesNoHalfWrittenSuggestions()
    {
        // The recorder stages rows into the caller's transaction. If it throws mid-way, whatever it
        // staged has to be detached or the order's own SaveChanges inherits the failure.
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);
        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme GmbH", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var scorer = new CountingSuggestionService(new ThrowingSuggestionService())
        {
            Returns = new[]
            {
                new SupplierSuggestion(supplier.Id, "Acme GmbH", 1, 0.55, "because",
                    Array.Empty<SupplierSignalContribution>()),
            },
        };

        var result = await BuildIngestion(db, scorer).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());
    }

    // ── Sender domain capture (D2) ────────────────────────────────────────────

    [Fact]
    public async Task SenderDomain_isStoredCanonically_withItsOwnRetentionClock()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);

        var result = await BuildIngestion(db, null).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None,
            inboundSenderDomain: "WWW.Acme.EXAMPLE");

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Equal("acme.example", order.InboundSenderDomain);
        Assert.NotNull(order.InboundSenderDomainCapturedAt);
    }

    [Fact]
    public async Task NoSenderDomain_leavesBothColumnsNull()
    {
        // Every non-email ingest path lands here — nothing must be invented for it.
        await using var db = NewDb();
        var orgId = await SeedOrgAsync(db);

        var result = await BuildIngestion(db, null).CreateStubFromParsedOrderAsync(
            orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None);

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Null(order.InboundSenderDomain);
        Assert.Null(order.InboundSenderDomainCapturedAt);
    }
}
