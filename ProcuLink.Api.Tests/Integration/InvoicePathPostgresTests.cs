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
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// An invoice travels the order pipeline but takes the invoice path: it goes to the org's invoice
/// destination whoever sent it, carries its codes through untranslated, and is held only when its
/// own checks fail. Real Postgres: the parse persist is Npgsql-only bulk SQL.
/// </summary>
[Collection("postgres-container")]
public sealed class InvoicePathPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_invoice_path");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_databaseConnectionString);

    [DockerRequiredFact]
    public async Task ParsedInvoice_RoutesToInvoiceDestination_EvenWhenVendorIsASupplier()
    {
        var (orgId, vendorId, destinationId) = await SeedAsync("route");

        // A balanced credit note, arriving on a channel that chose the vendor as the supplier.
        Guid orderId;
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildIngestion(db).CreateStubFromParsedOrderAsync(
                orgId, vendorId, Invoice(-60m, -40m, -100m, -21m, -121m), "rest_ingress", CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            orderId = result.Value!.Id;
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var order = await verify.PurchaseOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        Assert.Equal(destinationId, order.SupplierId);
        Assert.All(order.Lines, l => Assert.False(l.NeedsReview));
        Assert.Equal(OrderStatusConstants.Ready, order.Status);
        Assert.Equal(-121m, order.GrandTotal);   // a credit note's total is not "uncaptured"
    }

    [DockerRequiredFact]
    public async Task ParsedInvoice_FailingCheck_IsHeldWithReason()
    {
        var (orgId, vendorId, destinationId) = await SeedAsync("held");

        // An uploaded PDF whose lines (50 + 40) do not add up to its net total (100).
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seed = new ProcuLinkDbContext(_options!))
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = vendorId, PoNumber = "PLACEHOLDER", Currency = "EUR",
                Status = OrderStatusConstants.Parsing, SourceFileKey = $"{orgId}/{orderId}/invoice.pdf",
                OrderDate = DateOnly.FromDateTime(now), CreatedAt = now, UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildIngestion(db, Invoice(50m, 40m, 100m, 21m, 121m))
                .ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.PendingReview, order.Status);
        Assert.Equal(destinationId, order.SupplierId);
        var open = await verify.OrderExceptions.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.OrderId == orderId && e.State == "open").ToListAsync();
        var check = Assert.Single(open);
        Assert.Equal("invoice_check", check.Code);
        Assert.Equal("Line amounts do not add up to the net total.", check.Message);
    }

    [DockerRequiredFact]
    public async Task HeaderEdit_FixingTotal_ClearsCheckAndReleasesInvoice()
    {
        var (orgId, vendorId, _) = await SeedAsync("fix");

        // Held invoice: net 100 + VAT 21 should total 121, but the document says 120.
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seed = new ProcuLinkDbContext(_options!))
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = vendorId, PoNumber = "INV-2026-002", Currency = "EUR",
                DocumentType = "invoice", Status = OrderStatusConstants.PendingReview, SupplierName = "Example Supplies GmbH",
                SubTotal = 100m, TaxTotal = 21m, GrandTotal = 120m, OrderDate = DateOnly.FromDateTime(now),
                CreatedAt = now, UpdatedAt = now,
                Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = 100m, LineAmount = 100m } },
            });
            seed.OrderExceptions.Add(new OrderException
            {
                Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId, Stage = "Validate", Code = "invoice_check",
                Severity = "warning", State = "open", Message = "Net plus VAT does not equal the total.", CreatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        // A resolve that edits nothing about the figures leaves the invoice held.
        await using (var db = new ProcuLinkDbContext(_options!))
            Assert.True((await BuildResolution(db).ResolveAsync(orgId, orderId,
                new[] { new LineResolution(1, "ITEM-A") }, false, CancellationToken.None)).IsSuccess);
        await using (var check = new ProcuLinkDbContext(_options!))
            Assert.Equal(OrderStatusConstants.PendingReview,
                (await check.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);

        // Correcting the total clears the check and releases it.
        await using (var db = new ProcuLinkDbContext(_options!))
            Assert.True((await BuildResolution(db).ResolveAsync(orgId, orderId,
                Array.Empty<LineResolution>(), false, CancellationToken.None,
                new ResolveHeaderFields(GrandTotal: 121m))).IsSuccess);

        await using var verify = new ProcuLinkDbContext(_options!);
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(121m, order.GrandTotal);
        Assert.NotEqual(OrderStatusConstants.PendingReview, order.Status);
        Assert.DoesNotContain(await verify.OrderExceptions.AsNoTracking()
            .Where(e => e.OrderId == orderId && e.Code == "invoice_check").ToListAsync(), e => e.State == "open");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static ExtractedOrder Invoice(decimal line1, decimal line2, decimal net, decimal tax, decimal total) => new(
        PoNumber: "INV-2026-001",
        OrderDate: new DateTime(2026, 11, 4),
        BuyerName: "Example Buyer Oy",
        Currency: "EUR",
        Lines: new[]
        {
            new ExtractedOrderLine(1, "ITEM-A", "Item A", 1m, "pcs", line1, LineAmount: line1),
            new ExtractedOrderLine(2, "ITEM-B", "Item B", 1m, "pcs", line2, LineAmount: line2),
        },
        SupplierName: "Example Supplies GmbH",
        SubTotal: net, TaxTotal: tax, GrandTotal: total,
        DocumentType: "invoice",
        Iban: "DE89370400440532013000");

    private async Task<(Guid orgId, Guid vendorId, Guid destinationId)> SeedAsync(string slug)
    {
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{slug}_{orgId:N}", Name = slug, Slug = $"{slug}-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
            InvoiceDestinationSupplierId = destinationId,
        });
        db.Suppliers.Add(new Supplier { Id = vendorId, OrgId = orgId, Name = "Example Supplies GmbH", CreatedAt = now });
        db.Suppliers.Add(new Supplier { Id = destinationId, OrgId = orgId, Name = "Accounting system", CreatedAt = now });
        await db.SaveChangesAsync();
        return (orgId, vendorId, destinationId);
    }

    private static OrderResolutionService BuildResolution(ProcuLinkDbContext db) => new(
        db, new ItemMappingService(db), NullLogger<OrderService>.Instance,
        new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
        new AiSuggestionDecisionService(db));

    private static OrderIngestionService BuildIngestion(ProcuLinkDbContext db, ExtractedOrder? pdf = null)
    {
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1 }));

        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(pdf is not null);
        extractor.Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructuredExtractionResult(true, 0.9, pdf, null, Array.Empty<int>()));

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings.Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        return new OrderIngestionService(
            db,
            storage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new PdfOrderParser() }),
            itemMappings.Object,
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            extractor.Object,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: null);
    }
}
