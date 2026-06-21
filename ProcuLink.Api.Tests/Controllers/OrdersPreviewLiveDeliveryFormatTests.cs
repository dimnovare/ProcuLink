using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
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

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// MV-4 — LIVE (non-revision) preview format parity. The transform/deliver path resolves the output
/// format as explicit-request ?? supplier delivery-config format ?? safe default. The live mapping
/// editor always POSTs the FE default <c>format=csv</c>, so before MV-4 a non-revision supplier that
/// DELIVERS a non-CSV format (json/xml/cxml/…) previewed as CSV — preview ≠ delivered bytes. With
/// <c>honorFormat=false</c> the preview must now swap to the supplier's live delivery-config format
/// (the same source the transform action uses), and the preview bytes must equal the real transform's
/// bytes in that format. <c>honorFormat=true</c> (the user explicitly exploring a format) still
/// renders the requested format verbatim — the exploratory opt-out is unchanged.
/// </summary>
public class OrdersPreviewLiveDeliveryFormatTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ITransformService[] RealTransformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new OrderMappingOverrideService(db),
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            RealTransformers());
    }

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) BuildOrderService(ProcuLinkDbContext db)
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
            RealTransformers(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    /// <summary>Seeds a resolved, NON-revision order (no ConnectionRevisionId) with a supplier-promoted
    /// output mapping and a live delivery config whose OutputFormat is <paramref name="deliveryFormat"/>
    /// (null ⇒ no delivery-config row at all).</summary>
    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedAsync(
        ProcuLinkDbContext db, string? deliveryFormat)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Live Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-LIVE-1",
            BuyerName  = "Live Buyer",
            OrderDate  = new DateOnly(2026, 6, 21),
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
                    Quantity = 4m, Unit = "EA", UnitPrice = 12m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        if (deliveryFormat is not null)
        {
            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Protocol = "http", OutputFormat = deliveryFormat,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        // Supplier-promoted output (format-agnostic field rules) — drives BOTH preview and transform
        // when there is no per-order override, exactly like a live supplier.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"]   = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        return (orgId, supplierId, orderId);
    }

    private static (string Format, string? Content) ReadPreview(IActionResult result)
    {
        var ok   = Assert.IsType<OkObjectResult>(result);
        var type = ok.Value!.GetType();
        var format  = type.GetProperty("format")!.GetValue(ok.Value) as string;
        var content = type.GetProperty("content")?.GetValue(ok.Value) as string;
        Assert.NotNull(format);
        return (format!, content);
    }

    /// <summary>Blanks ISO-8601 timestamps so a preview vs delivery comparison ignores ONLY the
    /// unavoidable wall-clock skew (the JSON emitter stamps a generation time); everything structural
    /// — field layout, values, ordering — must still match byte-for-byte.</summary>
    private static string StripTimestamps(string s) =>
        System.Text.RegularExpressions.Regex.Replace(
            s, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z?", "<TS>");

    // ── honorFormat=false: non-revision preview swaps to the supplier's live delivery format ──────

    [Fact]
    public async Task Preview_NonRevision_HonorFormatFalse_SwapsToSupplierDeliveryFormat_AndMatchesTransform()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedAsync(db, deliveryFormat: "json");

        // FE always POSTs format=csv as its default; honorFormat=false = "show me what gets delivered".
        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(
            orderId, request: null, format: "csv", honorFormat: false, ct: CancellationToken.None);
        var (previewFormat, previewContent) = ReadPreview(preview);

        Assert.Equal("Json", previewFormat);          // swapped to the live delivery format, NOT csv
        Assert.NotNull(previewContent);

        // preview == delivery: the real transform in the resolved (json) format.
        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        var delivered = Encoding.UTF8.GetString(captured()!);

        // Structural parity, ignoring only the emitter's generation timestamp (preview and transform
        // run microseconds apart). If the swap had NOT happened the preview would be CSV and this would
        // diverge wholesale, not just on the timestamp.
        Assert.Equal(StripTimestamps(delivered), StripTimestamps(previewContent!));
    }

    // ── honorFormat=true: the exploratory opt-out still renders the requested format ──────────────

    [Fact]
    public async Task Preview_NonRevision_HonorFormatTrue_KeepsRequestedCsv()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedAsync(db, deliveryFormat: "json");

        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(
            orderId, request: null, format: "csv", honorFormat: true, ct: CancellationToken.None);
        var (previewFormat, _) = ReadPreview(preview);

        Assert.Equal("Csv", previewFormat);           // explicit exploration wins — no swap
    }

    // ── no delivery-config format → keep the requested csv default (no regression) ────────────────

    [Fact]
    public async Task Preview_NonRevision_NoDeliveryConfig_KeepsRequestedCsv()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedAsync(db, deliveryFormat: null);

        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(
            orderId, request: null, format: "csv", honorFormat: false, ct: CancellationToken.None);
        var (previewFormat, _) = ReadPreview(preview);

        Assert.Equal("Csv", previewFormat);           // nothing to swap to → unchanged behaviour
    }
}
