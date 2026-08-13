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
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase 1 ingest wiring: the SYNC ingest path (<c>CreateStubFromParsedOrderAsync</c>, used by the
/// email/REST/PDF-AI ingress) must carry the widened lossless fields from the
/// <see cref="ExtractedOrder"/> onto the persisted entity — the header terms/contact columns, an
/// <see cref="OrderParty"/> child row per address, the per-line manufacturer part number etc., and a
/// one-per-order <see cref="SourceCapture"/> raw bag built from the LLM <c>raw_fields</c>. Runs on the
/// real (non-Ignored) DbContext via InMemory: the sync path attaches the navs inline and persists
/// them in a single SaveChanges, so no Npgsql-only bulk op is exercised here (the Postgres
/// delete-then-insert async path is covered by the Postgres integration test).
/// </summary>
public class OrderIngestionLosslessCaptureTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();

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

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplier(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "LosslessCo", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    [Fact]
    public async Task SyncIngest_attaches_parties_sourceCapture_and_line_lossless_columns()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        var extracted = new ExtractedOrder(
            PoNumber: "4730154181",
            OrderDate: new DateTime(2026, 6, 12),
            BuyerName: "Exemplar Stahl GmbH",
            Currency: "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(
                    LineNumber: 1, BuyerItemCode: "00010", Description: "Lucerne",
                    Quantity: 1, Unit: "ST", UnitPrice: 306.28m,
                    ManufacturerPartNumber: "PLKMX94EGK",
                    Recipient: "c.testperson@buyer.example.com"),
            },
            ContactEmail: "c.testperson@buyer.example.com",
            Incoterms: "DDP",
            Parties: new[]
            {
                new ExtractedParty("shipTo", Name: "Exemplar Stahl GmbH", City: "Teststadt", Vat: "ATU99000000"),
            },
            RawFields: new[] { new ExtractedRawField("EDI id", "999000111") });

        var svc = BuildService(db);
        var result = await svc.CreateStubFromParsedOrderAsync(orgId, supplierId, extracted, "email", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var orderId = result.Value!.Id;

        // Reload from the context — proves the navs were persisted, not just held in memory.
        var order = await db.PurchaseOrders
            .Include(o => o.Parties)
            .Include(o => o.Lines)
            .Include(o => o.SourceCapture)
            .SingleAsync(o => o.Id == orderId);

        // Header lossless columns.
        Assert.Equal("DDP", order.Incoterms);
        Assert.Equal("c.testperson@buyer.example.com", order.ContactEmail);

        // Party child row.
        var party = Assert.Single(order.Parties);
        Assert.Equal("shipTo", party.Role);
        Assert.Equal("ATU99000000", party.Vat);
        Assert.Equal(orgId, party.OrgId);

        // Per-line lossless columns.
        var line = Assert.Single(order.Lines);
        Assert.Equal("PLKMX94EGK", line.ManufacturerPartNumber);
        Assert.Equal("c.testperson@buyer.example.com", line.Recipient);

        // SourceCapture raw bag built from raw_fields.
        Assert.NotNull(order.SourceCapture);
        Assert.Equal(orgId, order.SourceCapture!.OrgId);
        Assert.Equal("pdf", order.SourceCapture.Format);
        Assert.NotNull(order.SourceCapture.TokensJson);
        Assert.Contains("999000111", order.SourceCapture.TokensJson!.RootElement.GetRawText());
        Assert.Contains("EDI id", order.SourceCapture.TokensJson.RootElement.GetRawText());
    }

    [Fact]
    public async Task SyncIngest_with_no_raw_fields_attaches_no_sourceCapture()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        var extracted = new ExtractedOrder(
            PoNumber: "PO-NOEXTRA",
            OrderDate: new DateTime(2026, 6, 12),
            BuyerName: "Acme",
            Currency: "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(1, "B1", "Widget", 2m, "PC", 5m),
            });

        var svc = BuildService(db);
        var result = await svc.CreateStubFromParsedOrderAsync(orgId, supplierId, extracted, "rest", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = await db.PurchaseOrders
            .Include(o => o.Parties)
            .Include(o => o.SourceCapture)
            .SingleAsync(o => o.Id == result.Value!.Id);

        Assert.Empty(order.Parties);
        Assert.Null(order.SourceCapture); // no raw_fields → no raw bag row (don't write empty captures)
    }

    /// <summary>
    /// BE security backlog — tenancy guard. The stub created by the SYNC ingest path
    /// (email/REST/PDF-AI) MUST carry the caller's <c>OrgId</c> on the FIRST and only
    /// <c>SaveChangesAsync</c>. A stub persisted with an empty/wrong org_id is a
    /// cross-tenant leak (it would surface in another tenant's order lists / queries).
    /// This pins that the persisted row — reloaded from the store, not just the
    /// in-memory return value — has org_id == the caller's org and non-empty.
    /// </summary>
    [Fact]
    public async Task SyncIngest_persisted_stub_carries_callers_OrgId()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        var extracted = new ExtractedOrder(
            PoNumber: "PO-TENANCY",
            OrderDate: new DateTime(2026, 6, 12),
            BuyerName: "Acme",
            Currency: "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(1, "B1", "Widget", 2m, "PC", 5m),
            });

        var svc = BuildService(db);
        var result = await svc.CreateStubFromParsedOrderAsync(orgId, supplierId, extracted, "rest", CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Reload from the context — proves org_id was persisted, not just set in memory.
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);

        Assert.NotEqual(Guid.Empty, order.OrgId);
        Assert.Equal(orgId, order.OrgId);

        // Belt-and-braces: the org-scoped query the rest of the app uses must find it,
        // and a different tenant's org-scoped query must NOT.
        var otherOrgId = Guid.NewGuid();
        Assert.True(await db.PurchaseOrders.AnyAsync(o => o.Id == order.Id && o.OrgId == orgId));
        Assert.False(await db.PurchaseOrders.AnyAsync(o => o.Id == order.Id && o.OrgId == otherOrgId));
    }
}
