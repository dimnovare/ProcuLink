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
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase 2 transform routing for the per-order mapping override (heart-piece-flex).
///
/// The CRITICAL regression: an order with NO override must produce output BYTE-IDENTICAL to the
/// fixed transformer (CSV + JSON). A second pair of tests proves an order WITH a usable override
/// routes to <see cref="MappedTransformService"/> instead, and that an override carrying ONLY custom
/// fields (no output mapping) does NOT divert the transform.
/// </summary>
public class OrderServiceMappingOverrideTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Builds OrderService wired with the REAL CSV + JSON transformers and a byte-capturing storage mock.</summary>
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

        var transformers = new ITransformService[]
        {
            new CsvTransformService(),
            new JsonTransformService(),
        };

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            transformers,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    private static async Task<(Guid orgId, Guid orderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme Supplier", CreatedAt = DateTime.UtcNow });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-REG-1",
            BuyerName  = "Acme Buyer",
            OrderDate  = new DateOnly(2026, 4, 2),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    /// <summary>Builds the expected fixed-transformer bytes directly, from a tracking-free clone of the order.</summary>
    private static async Task<byte[]> FixedTransformBytesAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId, OutputFormat format)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .FirstAsync(o => o.Id == orderId && o.OrgId == orgId);

        ITransformService fixedSvc = format == OutputFormat.Csv
            ? new CsvTransformService()
            : new JsonTransformService();

        var result = await fixedSvc.TransformAsync(order, format, CancellationToken.None);
        result.Content.Position = 0;
        using var ms = new MemoryStream();
        await result.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task TransformAsync_NoOverride_Csv_ProducesByteIdenticalOutput_ToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var actual = captured();
        Assert.NotNull(actual);
        Assert.Equal(expected, actual); // byte-for-byte identical (CSV has no timestamp)
    }

    [Fact]
    public async Task TransformAsync_NoOverride_Json_IsIdenticalToFixedTransformer_ExceptGeneratedAtTimestamp()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Json);

        var (svc, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var actual = captured();
        Assert.NotNull(actual);

        // The JSON transformer itself stamps a non-deterministic generatedAt; everything else must match
        // byte-for-byte. Normalising that one field proves the no-override path is the fixed transformer.
        Assert.Equal(StripGeneratedAt(expected), StripGeneratedAt(actual!));
    }

    /// <summary>Re-serialises the JSON with the non-deterministic <c>generatedAt</c> property removed.</summary>
    private static string StripGeneratedAt(byte[] json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("generatedAt")) continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task TransformAsync_OverrideWithOnlyCustomFields_StillUsesFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        // Store an override with custom fields but NO output mapping → must NOT divert the transform.
        var overrideSvc = new OrderMappingOverrideService(db);
        await overrideSvc.UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            CustomFields = { new CustomField { Key = "gln", Label = "GLN", Scope = "header", Value = "X" } },
            Output = null,
        }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured()); // unchanged from fixed transformer
    }

    [Fact]
    public async Task TransformAsync_OverrideWithOutputMapping_RoutesToMappedTransformService()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var fixedBytes = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var overrideSvc = new OrderMappingOverrideService(db);
        await overrideSvc.UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var actual = captured();
        Assert.NotNull(actual);

        var csv = Encoding.UTF8.GetString(actual!);
        // Override-driven header row uses the override's output paths, NOT the fixed columns.
        Assert.StartsWith("OrderRef,ItemCode", csv);
        Assert.Contains("PO-REG-1,SUP-1", csv);
        Assert.NotEqual(fixedBytes, actual); // genuinely different from the fixed transformer
    }

    [Fact]
    public async Task TransformAsync_WholeDocumentTemplate_RoutesToTemplateService_EvenForUnregisteredFormat()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        // A whole-document template + an XML content type. Template mode must render the document
        // itself — it does NOT need a registered XML transformer (only CSV + JSON are wired here).
        var overrideSvc = new OrderMappingOverrideService(db);
        await overrideSvc.UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            OutputTemplate =
                "<order ref=\"{{OrderNr}}\">" +
                "{{ for L in Lines }}<line code=\"{{L.DistributorPid}}\" qty=\"{{L.Qty}}\"/>{{ end }}" +
                "</order>",
            OutputTemplateContentType = "application/xml",
        }, CancellationToken.None);

        var (svc, captured) = Build(db); // only CSV + JSON transformers registered
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Xml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var actual = captured();
        Assert.NotNull(actual);

        var xml = Encoding.UTF8.GetString(actual!);
        Assert.Equal(
            "<order ref=\"PO-REG-1\"><line code=\"SUP-1\" qty=\"3\"/><line code=\"SUP-2\" qty=\"2\"/></order>",
            xml);
        Assert.Equal("xml", result.Value!.Format); // artifact format follows the requested OutputFormat
    }

    [Fact]
    public async Task TransformAsync_BrokenTemplate_FailsClearly_AndRevertsStatusToReady()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        var overrideSvc = new OrderMappingOverrideService(db);
        await overrideSvc.UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            OutputTemplate = "{{ for x in Lines }}oops", // unbalanced for/end → compile error
        }, CancellationToken.None);

        var (svc, _) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("compile", result.Error);

        // The order must NOT be left stuck in "transforming" — a broken template reverts to ready.
        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal("ready", reloaded.Status);
    }

    [Fact]
    public async Task TransformAsync_OverrideForUnsupportedFormat_FallsThroughToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);

        // A usable CSV/JSON-style override exists, but we transform to XML — override must NOT apply,
        // and since no XML transformer is registered here, the service reports the fixed-path failure.
        var overrideSvc = new OrderMappingOverrideService(db);
        await overrideSvc.UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var (svc, _) = Build(db); // only CSV + JSON transformers registered
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Xml, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("No transform service registered", result.Error);
    }
}
