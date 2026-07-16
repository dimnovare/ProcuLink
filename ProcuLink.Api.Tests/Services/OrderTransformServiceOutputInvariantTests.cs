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
/// B1 — output invariant on the FIXED CSV/JSON/XML transform path.
///
/// <para>The output guard <see cref="OutputFieldValidator.ValidateEntity"/> (UnitPrice &lt; 0 or
/// Quantity &lt;= 0 → hold for review) was previously wired ONLY into the X12 / UBL / cXML
/// transforms. It is now also applied inside the fixed CSV / JSON / XML transforms (where the
/// entity's canonical columns ARE the emitted bytes). It is deliberately NOT applied to the override /
/// whole-document-template / OutputNode-tree paths, which emit via a path that can legitimately
/// transform values or drop lines (IncludeWhen) — guarding the raw entity there would pre-empt those
/// features (the adversarial review caught exactly that when an earlier draft guarded centrally in
/// OrderTransformService).</para>
///
/// <para>A €0 unit price is NO LONGER held (founder-approved 2026-06): a legitimately-free line must
/// transform and deliver, with the non-blocking €0 warning surfaced separately by InvariantValidator.
/// A NEGATIVE price and a zero/negative quantity remain hard holds. These tests drive a plain
/// (no-override) order through <see cref="OrderService.TransformAsync"/>: a CSV/JSON order with a zero
/// price now transforms successfully and produces an artifact; a negative price or negative quantity is
/// HELD (<see cref="TransformValidationException"/> → status <c>transform_failed</c>, NO artifact),
/// while a fully-valid order still transforms (no false positive — valid bytes unchanged). A regression
/// pair confirms X12 is unchanged.</para>
///
/// <para>The hold lands in <c>transform_failed</c>, not <c>ready</c>: reverting to <c>ready</c> was
/// indistinguishable from "never transformed", so every one of these holds was invisible to
/// <c>OpsHealthService</c> (whose TransformFailed count was structurally always 0) and never opened an
/// exception row. The status stays re-claimable, so correcting the line and re-transforming works.</para>
/// </summary>
public class OrderTransformServiceOutputInvariantTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Builds OrderService with the REAL CSV + JSON + X12 transformers and a byte-capturing storage mock.</summary>
    private static (OrderService Svc, Func<int> UploadCount, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db)
    {
        byte[]? captured = null;
        var uploads = 0;

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            {
                uploads++;
                using var ms = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(ms);
                captured = ms.ToArray();
            })
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new X12TransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => uploads, () => captured);
    }

    /// <summary>
    /// Seeds a fully-resolved 2-line order. <paramref name="line1Price"/> / <paramref name="line2Quantity"/>
    /// let a caller inject a bad price (line 1) or a bad quantity (line 2). Every line carries a
    /// BuyerItemCode so the X12 format-mandatory-code check is never the thing that trips.
    /// </summary>
    private static async Task<(Guid orgId, Guid orderId)> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, decimal line1Price = 10m, decimal line2Quantity = 2m)
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
            PoNumber   = "PO-B1-1",
            BuyerName  = "Acme Buyer",
            OrderDate  = new DateOnly(2026, 4, 2),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Ready,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = line1Price, NeedsReview = false, Confidence = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
                    Quantity = line2Quantity, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static async Task AssertHeldAsync(
        ProcuLinkDbContext db, OrderService svc, Func<int> uploadCount, Guid orgId, Guid orderId, OutputFormat format)
    {
        var result = await svc.TransformAsync(orgId, orderId, format, CancellationToken.None);

        // Held: the central guard threw → the transform fails cleanly and returns a failure (not a throw out).
        Assert.False(result.IsSuccess);
        Assert.Contains("Cannot transform", result.Error);

        // The hold is VISIBLE: never stuck in 'transforming', never advanced to ready_to_deliver, and
        // never quietly back in 'ready' — a bad price/quantity is a genuine fault needing a human, and
        // 'ready' is indistinguishable from "never transformed", which kept these holds out of ops
        // health entirely. transform_failed counts on the health tile and opens an exception row.
        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.TransformFailed, reloaded.Status);

        // No artifact uploaded and no artifact row persisted — nothing was delivered.
        Assert.Equal(0, uploadCount());
        var artifacts = await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == orderId).CountAsync();
        Assert.Equal(0, artifacts);
    }

    // ── CSV / JSON: €0 now transforms; negative price / negative qty stay HELD ──

    [Fact]
    public async Task TransformAsync_Csv_ZeroPriceLine_NowTransformsSuccessfully_AndProducesArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line1Price: 0m);
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());

        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, reloaded.Status);

        var artifacts = await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == orderId).CountAsync();
        Assert.Equal(1, artifacts);
    }

    [Fact]
    public async Task TransformAsync_Csv_NegativePriceLine_IsHeld_MarksTransformFailed_NoArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line1Price: -5m);
        var (svc, uploads, _) = Build(db);

        await AssertHeldAsync(db, svc, uploads, orgId, orderId, OutputFormat.Csv);
    }

    [Fact]
    public async Task TransformAsync_Csv_NegativeQuantityLine_IsHeld_MarksTransformFailed_NoArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line2Quantity: -1m);
        var (svc, uploads, _) = Build(db);

        await AssertHeldAsync(db, svc, uploads, orgId, orderId, OutputFormat.Csv);
    }

    [Fact]
    public async Task TransformAsync_Json_ZeroPriceLine_NowTransformsSuccessfully_AndProducesArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line1Price: 0m);
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());

        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, reloaded.Status);
    }

    [Fact]
    public async Task TransformAsync_Json_NegativeQuantityLine_IsHeld_MarksTransformFailed_NoArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line2Quantity: -1m);
        var (svc, uploads, _) = Build(db);

        await AssertHeldAsync(db, svc, uploads, orgId, orderId, OutputFormat.Json);
    }

    // ── No false positive: a fully-valid order still transforms ────────────────

    [Fact]
    public async Task TransformAsync_Csv_FullyValidOrder_TransformsSuccessfully_AndProducesArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db); // all prices/quantities positive
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());

        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, reloaded.Status);

        var artifacts = await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == orderId).CountAsync();
        Assert.Equal(1, artifacts);
    }

    [Fact]
    public async Task TransformAsync_Json_FullyValidOrder_TransformsSuccessfully_AndProducesArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());

        var reloaded = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, reloaded.Status);
    }

    // ── X12 regression: unchanged behaviour (valid transforms, invalid throws) ──

    [Fact]
    public async Task TransformAsync_X12_FullyValidOrder_StillTransformsSuccessfully()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db);
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.X12, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());
    }

    [Fact]
    public async Task TransformAsync_X12_ZeroPriceLine_NowTransformsSuccessfully()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line1Price: 0m);
        var (svc, uploads, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.X12, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, uploads());
        Assert.NotNull(captured());
    }

    [Fact]
    public async Task TransformAsync_X12_NegativePriceLine_StillHeld_MarksTransformFailed_NoArtifact()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedResolvedOrderAsync(db, line1Price: -5m);
        var (svc, uploads, _) = Build(db);

        await AssertHeldAsync(db, svc, uploads, orgId, orderId, OutputFormat.X12);
    }
}
