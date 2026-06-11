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
/// Launch batch 4A — PREVIEW PARITY. The mapping-override preview must resolve the SAME effective
/// priority as the real transform (per-order override → supplier-promoted output → fixed), so what
/// the user previews is exactly what delivery produces. The strongest assertion here compares the
/// preview's content STRING against the REAL <c>OrderService.TransformAsync</c> artifact bytes for
/// the same order — true preview == delivery parity, not a re-implementation of the expectation.
/// </summary>
public class OrdersPreviewSupplierPromotedParityTests
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
            new OrderMappingOverrideService(db), // real — stored overrides must be honoured
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            RealTransformers());
        // _poMappings falls back to the REAL PoMappingService over the same db — the seam under test.
    }

    /// <summary>The REAL transform pipeline with a byte-capturing storage mock — the delivery side of parity.</summary>
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
            new PoMappingService(db), // real — same seam as the controller fallback
            new Mock<IAiMappingService>().Object,
            RealTransformers(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Parity Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-PARITY-1",
            BuyerName  = "Parity Buyer",
            OrderDate  = new DateOnly(2026, 6, 11),
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

        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    private static OutputMappingConfig PromotedOutput() => new()
    {
        Header = { ["po"]   = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
        Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
    };

    private static string PreviewContent(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var contentProp = ok.Value!.GetType().GetProperty("content");
        Assert.NotNull(contentProp);
        var content = contentProp!.GetValue(ok.Value) as string;
        Assert.NotNull(content);
        return content!;
    }

    // ── preview == delivery: supplier-promoted output, no per-order override, NO body ─────────

    [Fact]
    public async Task Preview_NoBody_NoOverride_SupplierPromotedOutput_MatchesRealTransformBytes()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        // PREVIEW side — no body, no stored override: pre-4A this 400'd; now the promoted
        // supplier mapping drives it.
        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.StartsWith("OrderRef,ItemCode", previewContent);

        // DELIVERY side — the REAL transform for the same order.
        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        var deliveredContent = Encoding.UTF8.GetString(captured()!);

        Assert.Equal(deliveredContent, previewContent); // preview == delivery, character for character
    }

    // ── preview == delivery: per-order override outranks the promoted mapping in BOTH paths ───

    [Fact]
    public async Task Preview_StoredOverrideAndPromotedOutput_OverrideWins_AndMatchesRealTransform()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"]   = new OutputFieldRule { OutputPath = "PerOrderRef",  CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PerOrderCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.StartsWith("PerOrderRef,PerOrderCode", previewContent); // override layout, not promoted

        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        var deliveredContent = Encoding.UTF8.GetString(captured()!);

        Assert.Equal(deliveredContent, previewContent);
    }

    // ── contract preserved: no body + no override + no promoted mapping → still 400 ───────────

    [Fact]
    public async Task Preview_NoBody_NoOverride_NoPromotedMapping_StillReturns400()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedResolvedOrderAsync(db);

        var controller = BuildController(db, orgId);
        var result = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result); // the pre-4A contract is unchanged
    }

    // ── a draft WITH usable output still wins over the promoted mapping in the preview ────────

    [Fact]
    public async Task Preview_DraftWithUsableOutput_OutranksPromotedMapping()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var draft = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "DraftRef", CanonicalField = "PoNumber" } },
            },
        };

        var controller = BuildController(db, orgId);
        var preview = await controller.PreviewMappingOverride(orderId, draft, "csv", CancellationToken.None);
        var previewContent = PreviewContent(preview);

        Assert.StartsWith("DraftRef", previewContent);          // the user's draft drives the preview
        Assert.DoesNotContain("OrderRef,ItemCode", previewContent); // promoted layout NOT used
    }
}
