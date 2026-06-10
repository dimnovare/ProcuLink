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
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V1 pinning: at ingest the order is stamped with the supplier's ACTIVE connection
/// revision, ONCE; publishing a NEW revision afterwards must NOT change an already-created
/// order's pin (the reproducibility invariant). A zero-config supplier pins null (fall back
/// to live config).
/// </summary>
public class OrderIngestionPinsConnectionRevisionTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

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
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

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

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
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
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static IFileStorageService CsvStorage(out Mock<IFileStorageService> mock)
    {
        mock = new Mock<IFileStorageService>();
        mock.Setup(s => s.UploadAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string key, string _, CancellationToken _) => key);
        return mock.Object;
    }

    private static MemoryStream CsvFile() =>
        new(Encoding.UTF8.GetBytes("po_number,line_no,sku,qty,unit_price\nPO-1,1,WIDGET,2,9.99\n"));

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplier(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "PinCo", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static ConnectionRevisionDraftInput SimpleBundle(string fmt) => new(
        "{}", null, fmt, "http", "{}", false, null, null, null, "live",
        new List<ConnectionItemMappingInput>());

    [Fact]
    public async Task CreateFromFile_PinsActiveRevision()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        // Publish a connection revision for the supplier.
        var conn = new SupplierConnectionService(db);
        var c = await conn.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await conn.CreateDraftAsync(orgId, c!.Id, SimpleBundle("xml"), false, "u", CancellationToken.None);
        await conn.PublishAsync(orgId, c.Id, v1!.Id, "u", CancellationToken.None);

        var svc = BuildService(db, CsvStorage(out _));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, CsvFile(), "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Equal(v1.Id, order.ConnectionRevisionId);
    }

    [Fact]
    public async Task PublishingNewRevision_DoesNotRepinExistingOrder()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        var conn = new SupplierConnectionService(db);
        var c = await conn.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await conn.CreateDraftAsync(orgId, c!.Id, SimpleBundle("xml"), false, "u", CancellationToken.None);
        await conn.PublishAsync(orgId, c.Id, v1!.Id, "u", CancellationToken.None);

        var svc = BuildService(db, CsvStorage(out _));
        var first = await svc.CreateFromFileAsync(orgId, supplierId, CsvFile(), "order1.csv", "text/csv", CancellationToken.None);
        Assert.True(first.IsSuccess);
        var firstOrderId = first.Value!.Id;

        // Publish a NEW revision (active flips to v2).
        var v2 = await conn.CreateDraftAsync(orgId, c.Id, SimpleBundle("csv"), false, "u", CancellationToken.None);
        await conn.PublishAsync(orgId, c.Id, v2!.Id, "u", CancellationToken.None);

        // The first order's pin must still point at v1 (reproducibility).
        var firstOrder = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == firstOrderId);
        Assert.Equal(v1.Id, firstOrder.ConnectionRevisionId);

        // A new order pins v2.
        var second = await svc.CreateFromFileAsync(orgId, supplierId, CsvFile(), "order2.csv", "text/csv", CancellationToken.None);
        var secondOrder = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == second.Value!.Id);
        Assert.Equal(v2.Id, secondOrder.ConnectionRevisionId);
    }

    [Fact]
    public async Task ZeroConfigSupplier_PinsNull()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);
        // No connection published for this supplier.

        var svc = BuildService(db, CsvStorage(out _));
        var result = await svc.CreateFromFileAsync(orgId, supplierId, CsvFile(), "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Null(order.ConnectionRevisionId); // fall back to live config — unchanged behaviour
    }

    [Fact]
    public async Task CreateStub_PinsActiveRevision()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplier(db);

        var conn = new SupplierConnectionService(db);
        var c = await conn.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await conn.CreateDraftAsync(orgId, c!.Id, SimpleBundle("xml"), false, "u", CancellationToken.None);
        await conn.PublishAsync(orgId, c.Id, v1!.Id, "u", CancellationToken.None);

        var svc = BuildService(db, CsvStorage(out _));
        var result = await svc.CreateStubAsync(orgId, supplierId, CsvFile(), "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == result.Value!.Id);
        Assert.Equal(v1.Id, order.ConnectionRevisionId);
    }
}
