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
/// Tenant-isolation regression tests for OrderService stub creation.
///
/// Background: CreateStubAsync and CreateStubFromParsedOrderAsync used to resolve
/// the supplier with <c>_db.Suppliers.FindAsync(supplierId)</c>, which looks up by
/// primary key only — no tenant scope. An authenticated user in org A could pass
/// org B's supplier UUID and create an order stamped OrgId=A but pointing at a
/// supplier owned by org B, reaching org B's delivery config / credentials /
/// mapping / AI candidates downstream.
///
/// These tests assert that a supplier belonging to a DIFFERENT org is treated as
/// "not found" and that no order row is created.
/// </summary>
public class OrderServiceCrossTenantSupplierTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(
        ProcuLinkDbContext db,
        IFileStorageService fileStorage)
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
            .Setup(s => s.GetAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
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

    // ── CreateStubAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateStubAsync_SupplierBelongsToDifferentOrg_ReturnsNotFound_AndCreatesNoOrder()
    {
        var db        = NewDb();
        var attackerOrgId = Guid.NewGuid();
        var victimOrgId   = Guid.NewGuid();
        var supplierId    = Guid.NewGuid();

        // Supplier owned by the VICTIM org.
        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = victimOrgId,
            Name      = "Victim Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db, FileStorageMock().Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("item,qty\nA,1\n"));
        var result = await svc.CreateStubAsync(
            attackerOrgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Supplier not found.", result.Error);

        // No order may exist for either org.
        Assert.Empty(await db.PurchaseOrders.AsNoTracking().ToListAsync());
    }

    // ── CreateStubFromParsedOrderAsync ────────────────────────────────────────

    [Fact]
    public async Task CreateStubFromParsedOrderAsync_SupplierBelongsToDifferentOrg_ReturnsNotFound_AndCreatesNoOrder()
    {
        var db        = NewDb();
        var attackerOrgId = Guid.NewGuid();
        var victimOrgId   = Guid.NewGuid();
        var supplierId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = victimOrgId,
            Name      = "Victim Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db, FileStorageMock().Object);

        var extracted = new ExtractedOrder(
            PoNumber:  "PO-ATTACK",
            OrderDate: DateTime.UtcNow,
            BuyerName: "Attacker",
            Currency:  "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(1, "BUYER-1", "Widget", 2m, "EA", 9.99m),
            });

        var result = await svc.CreateStubFromParsedOrderAsync(
            attackerOrgId, supplierId, extracted, "email", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Supplier not found.", result.Error);

        Assert.Empty(await db.PurchaseOrders.AsNoTracking().ToListAsync());
    }

    // ── Positive control: same-org supplier still works ───────────────────────

    [Fact]
    public async Task CreateStubAsync_SupplierBelongsToSameOrg_Succeeds_AndCreatesOrder()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Own Supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var svc = BuildService(db, FileStorageMock().Object);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("item,qty\nA,1\n"));
        var result = await svc.CreateStubAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var order = Assert.Single(await db.PurchaseOrders.AsNoTracking().ToListAsync());
        Assert.Equal(orgId, order.OrgId);
        Assert.Equal(supplierId, order.SupplierId);
    }
}
