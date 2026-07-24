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
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The SYNC ingest path's supplier-less sibling. <c>CreateStubFromParsedOrderAsync</c> persists an
/// already-parsed order (email-body NLP, REST push) and used to require a supplier id, so a
/// prose-only email to an org with no resolvable supplier produced NO ORDER AT ALL — accepted and
/// audited, then silently dropped — while an ATTACHMENT on the same message was parked
/// <c>unrouted</c> via <c>CreateUnroutedStubAsync</c>. These pin the sibling that closes that gap:
/// <c>CreateUnroutedStubFromParsedOrderAsync</c> persists the same order with a NULL supplier, NO
/// pinned connection revision, and status <c>unrouted</c>, so the assign-supplier flow can resolve it.
///
/// <para>InMemory is sufficient here: the sync path attaches its navs inline and persists them with a
/// single <c>SaveChanges</c> (no Npgsql-only bulk op). The follow-on assign-supplier re-resolve, which
/// does use <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, is pinned on real Postgres by
/// <c>UnroutedParsedOrderAssignSupplierPostgresTests</c>.</para>
/// </summary>
public class OrderIngestionUnroutedParsedOrderTests
{
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
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            new Mock<IFileStorageService>().Object,
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

    private static ExtractedOrder BodyOrder(string poNumber = "PO-BODY-1") => new(
        PoNumber: poNumber,
        OrderDate: new DateTime(2026, 7, 24),
        BuyerName: "Acme Buyer",
        Currency: "EUR",
        Lines: new[]
        {
            new ExtractedOrderLine(1, "WIDGET-A", "Widget A", 3m, "pcs", 2.50m),
            new ExtractedOrderLine(2, "WIDGET-B", "Widget B", 1m, "pcs", 9.00m),
        });

    /// <summary>
    /// Seeds an org that HAS a supplier with a published connection revision, but the order is
    /// created through the unrouted entry point anyway. That is the honest shape of the bug: the
    /// pin must be absent because THIS ORDER has no supplier, not because the org has nothing to pin.
    /// </summary>
    private static async Task<(Guid orgId, Guid supplierId, Guid revisionId)> SeedOrgWithConnectedSupplier(
        ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Routed Co", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            ActiveRevisionId = revisionId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, revisionId);
    }

    // ── 1. The order exists at all — the whole point of closing the gap ──────

    [Fact]
    public async Task UnroutedParsedOrder_persists_with_null_supplier_and_unrouted_status()
    {
        var db = NewDb();
        var (orgId, _, _) = await SeedOrgWithConnectedSupplier(db);

        var result = await BuildService(db)
            .CreateUnroutedStubFromParsedOrderAsync(orgId, BodyOrder(), "email_body_nlp", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Reload from the store — proves it was persisted, not just built in memory.
        var order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Equal(orgId, order.OrgId);
        Assert.Null(order.SupplierId);
        Assert.Equal(OrderStatusConstants.Unrouted, order.Status);
        Assert.Equal("PO-BODY-1", order.PoNumber);
    }

    // ── 2. No supplier ⇒ nothing to pin ─────────────────────────────────────

    [Fact]
    public async Task UnroutedParsedOrder_pins_no_connection_revision()
    {
        var db = NewDb();
        var (orgId, _, revisionId) = await SeedOrgWithConnectedSupplier(db);

        var result = await BuildService(db)
            .CreateUnroutedStubFromParsedOrderAsync(orgId, BodyOrder(), "email_body_nlp", CancellationToken.None);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);

        // A revision belongs to a SUPPLIER connection. With no supplier there is no revision to
        // pin, and borrowing the org's only supplier's revision would silently bind the order to a
        // counterparty nobody chose. assign-supplier pins it when the operator picks one.
        Assert.Null(order.ConnectionRevisionId);
        Assert.NotEqual(revisionId, order.ConnectionRevisionId);
    }

    // ── 3. The extracted content survives the park ──────────────────────────

    [Fact]
    public async Task UnroutedParsedOrder_persists_its_lines_all_flagged_for_review()
    {
        var db = NewDb();
        var (orgId, _, _) = await SeedOrgWithConnectedSupplier(db);

        var result = await BuildService(db)
            .CreateUnroutedStubFromParsedOrderAsync(orgId, BodyOrder(), "email_body_nlp", CancellationToken.None);

        var order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == result.Value!.Id);

        // The triage queue must show WHAT arrived, otherwise the operator cannot tell which
        // supplier to assign. No line can be resolved yet — there is no supplier to resolve against.
        Assert.Equal(2, order.Lines.Count);
        Assert.All(order.Lines, l => Assert.True(l.NeedsReview));
        Assert.All(order.Lines, l => Assert.Null(l.SupplierItemCode));
        Assert.Contains(order.Lines, l => l.BuyerItemCode == "WIDGET-A" && l.Quantity == 3m);
    }

    // ── 4. An empty extraction is still refused ─────────────────────────────

    [Fact]
    public async Task UnroutedParsedOrder_with_no_lines_is_refused()
    {
        var db = NewDb();
        var (orgId, _, _) = await SeedOrgWithConnectedSupplier(db);

        var empty = new ExtractedOrder(
            PoNumber: "PO-EMPTY", OrderDate: null, BuyerName: null, Currency: "EUR",
            Lines: Array.Empty<ExtractedOrderLine>());

        var result = await BuildService(db)
            .CreateUnroutedStubFromParsedOrderAsync(orgId, empty, "email_body_nlp", CancellationToken.None);

        // Parking an order with nothing in it gives the operator no routing signal and would
        // meter as a real order. The line-count guard applies to both siblings.
        Assert.False(result.IsSuccess);
        Assert.False(await db.PurchaseOrders.AnyAsync(o => o.OrgId == orgId));
    }

    // ── 5. Regression: the ROUTED sibling is unchanged ──────────────────────

    [Fact]
    public async Task RoutedParsedOrder_still_pins_the_revision_and_is_not_unrouted()
    {
        var db = NewDb();
        var (orgId, supplierId, revisionId) = await SeedOrgWithConnectedSupplier(db);

        var result = await BuildService(db)
            .CreateStubFromParsedOrderAsync(orgId, supplierId, BodyOrder("PO-ROUTED-1"), "email_body_nlp", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);

        Assert.Equal(supplierId, order.SupplierId);
        Assert.Equal(revisionId, order.ConnectionRevisionId);
        Assert.NotEqual(OrderStatusConstants.Unrouted, order.Status);
    }
}
