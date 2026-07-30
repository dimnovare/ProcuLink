using System.Text;
using System.Text.Json;
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
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-12 P0 — "Save this layout for the supplier" must never brick the supplier.
///
/// <para><b>P0-1.</b> The promote/adopt predicate asked "is this NOT cXML/X12?" while
/// <c>OutputTemplateEmitter</c> answers "is this JSON, XML or CSV?". Four formats fell in the gap
/// (<c>Ubl</c>, <c>UblOrder</c>, <c>X12_850</c>, <c>EdifactOrders</c>): promote stored the tree and
/// reported "Saved … the file layout you designed", the transform adopted it, and the emitter threw
/// — a TERMINAL <c>transform_failed</c> on EVERY future order for that supplier, reported as
/// success, with no way to un-promote.</para>
///
/// <para><b>P0-2.</b> The artifact row and (post-#77) the delivered content type + filename come from
/// the CONNECTION's format, while the bytes come from the TREE's format. A promoted <c>Json</c> tree
/// on a cXML connection therefore shipped JSON bytes as <c>application/xml</c> named
/// <c>PO-x.xml</c>, recorded as <c>cxml</c>. The connection format wins: a tree that does not render
/// the connection's format must not drive the document.</para>
/// </summary>
public class PromotedOutputTreeP0Tests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db)
    {
        byte[]? captured = null;

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            {
                using var ms = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(ms);
                captured = ms.ToArray();
            })
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            new ITransformService[]
            {
                new CsvTransformService(), new JsonTransformService(),
                new XmlTransformService(), new CxmlTransformService(),
            },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    private static async Task<(Guid OrgId, Guid SupplierId)> SeedSupplierAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "P0 Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-P0", BuyerName = "P0 Buyer", Currency = "EUR",
            OrderDate = new DateOnly(2026, 7, 30), Status = OrderStatusConstants.Ready,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>An ordinary drawn layout. Only its <see cref="OutputNodeTemplate.Format"/> varies.</summary>
    private static OutputNodeTemplate DrawnTree(OutputFormat format) => new()
    {
        Format = format,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("orderNumber",
                new OutputFieldRule { OutputPath = "orderNumber", CanonicalField = "PoNumber" }),
            OutputNode.Arr("items", OutputNode.Obj("item",
                OutputNode.FieldOf("sku",
                    new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" })))),
    };

    // ══ P0-1 — a promote must never brick the supplier ═══════════════════════════════════════════

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public async Task PromotingATreeTheEmitterCannotRender_DoesNotClaimToHaveSavedALayout(OutputFormat format)
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = DrawnTree(format) }, CancellationToken.None);

        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.False(promoted!.OutputTreePromoted);
        Assert.DoesNotContain("the file layout you designed", promoted.Message);
    }

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public async Task PromotingATreeTheEmitterCannotRender_LeavesEveryFutureOrderDeliverable(OutputFormat format)
    {
        // THE P0. One click, and every subsequent order for this supplier died in the emitter with a
        // terminal transform_failed — no artifact, no delivery, and a success message on the button.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var designedOn = await SeedOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, designedOn,
            new OrderMappingOverride { OutputTree = DrawnTree(format) }, CancellationToken.None);
        await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, designedOn, CancellationToken.None);

        // The next file from the same supplier — no per-order override of any kind.
        var nextOrder = await SeedOrderAsync(db, orgId, supplierId);
        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, nextOrder, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.StartsWith("PoNumber,OrderDate", Encoding.UTF8.GetString(captured()!));

        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == nextOrder).Select(o => o.Status).SingleAsync();
        Assert.NotEqual(OrderStatusConstants.TransformFailed, status);
    }

    [Fact]
    public async Task ARenderableTreeStillPromotesAndStillDrivesTheDocument()
    {
        // Assert the DIFFERENCE: "refuse every tree" would pass both P0-1 tests above.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var designedOn = await SeedOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, designedOn,
            new OrderMappingOverride { OutputTree = DrawnTree(OutputFormat.Json) }, CancellationToken.None);
        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, designedOn, CancellationToken.None);

        Assert.True(promoted!.OutputTreePromoted);

        var nextOrder = await SeedOrderAsync(db, orgId, supplierId);
        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, nextOrder, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(captured()!));
        Assert.Equal("PO-P0", doc.RootElement.GetProperty("orderNumber").GetString());
    }

    // ══ P0-2 — the connection's format wins ══════════════════════════════════════════════════════

    [Fact]
    public async Task APromotedJsonTree_NeverWritesJsonBytesIntoACxmlArtifact()
    {
        // artifact.Format = effectiveFormat, and post-#77 delivery derives the content type and file
        // name from it. A promoted Json tree on a cXML connection shipped JSON bytes labelled cxml.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = DrawnTree(OutputFormat.Json) }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var bytes = Encoding.UTF8.GetString(captured()!);

        // The artifact says cxml; the bytes must actually BE cXML.
        var format = await db.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrderId == orderId).Select(a => a.Format).SingleAsync();
        Assert.Equal("cxml", format);
        Assert.Contains("<cXML", bytes);
        Assert.DoesNotContain("\"orderNumber\"", bytes);
    }

    [Fact]
    public async Task APromotedCsvTree_DoesNotDriveAJsonConnection()
    {
        // Mirror case between two formats the emitter DOES render, so this cannot be explained away
        // as a side effect of the P0-1 renderability gate.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = DrawnTree(OutputFormat.Csv) }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var bytes = Encoding.UTF8.GetString(captured()!);

        using var doc = JsonDocument.Parse(bytes); // must be JSON at all
        Assert.False(doc.RootElement.TryGetProperty("orderNumber", out _)); // the CSV tree did not drive it
    }

    [Fact]
    public async Task PromotingATreeThatDoesNotMatchTheSupplierFormat_IsRefusedAtPromoteTime()
    {
        // Guard at BOTH ends: the transform refuses to adopt it, and promote refuses to claim it, so
        // the operator learns at the click instead of discovering it in a delivered document.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", OutputFormat = "cxml",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = DrawnTree(OutputFormat.Json) }, CancellationToken.None);

        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.False(promoted!.OutputTreePromoted);

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.Null(stored?.OutputTree);
    }

    [Fact]
    public async Task PromotingATreeThatMatchesTheSupplierFormat_IsAccepted()
    {
        // Assert the DIFFERENCE for the guard above.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", OutputFormat = "json",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = DrawnTree(OutputFormat.Json) }, CancellationToken.None);

        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.True(promoted!.OutputTreePromoted);
    }
}
