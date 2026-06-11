using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Read-parity (launch batch 7 follow-up) — the mapping-override PREVIEW must resolve the SAME
/// effective priority the transform applies for a PINNED order under the
/// <c>Connections:RevisionAuthority</c> flag: per-order override → the PINNED revision's output
/// snapshot → fixed transformer, in the revision's snapshotted format — with the live
/// supplier-promoted mapping NOT consulted (reproducibility). The strongest assertions compare the
/// preview content STRING against the REAL <c>OrderService.TransformAsync</c> artifact bytes for the
/// same order, both running with the SAME resolver — true preview == delivery parity.
///
/// GOLDEN guard: flag OFF (or unpinned) previews stay byte-identical to the pre-read-parity
/// behaviour (supplier-promoted fallback, launch batch 4A), even when a divergent revision exists.
/// </summary>
public class OrdersPreviewRevisionAuthorityParityTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    private static ITransformService[] RealTransformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    /// <summary>Controller with the REAL stored-override + PoMapping seams and an optional revision-authority resolver.</summary>
    private static OrdersController BuildController(
        ProcuLinkDbContext db, Guid orgId, IEffectiveConnectionConfigResolver? resolver)
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
            RealTransformers(),
            effectiveConfig: resolver);
        // _poMappings falls back to the REAL PoMappingService over the same db (4A seam).
    }

    /// <summary>The REAL transform pipeline with a byte-capturing storage mock — the delivery side of parity.</summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) BuildOrderService(
        ProcuLinkDbContext db, IEffectiveConnectionConfigResolver resolver)
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
            new PoMappingService(db), // real — the supplier-promoted (4A) seam
            new Mock<IAiMappingService>().Object,
            RealTransformers(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: resolver);

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
            PoNumber   = "PO-RP-1",
            BuyerName  = "ReadParity Buyer",
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

    /// <summary>Seeds a published connection revision and pins the order to it (mirrors the ingest-time stamp).</summary>
    private static async Task<SupplierConnectionRevision> SeedPinnedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, Guid orderId,
        string? outputMappingJson, string? outputFormat = null)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id           = Guid.NewGuid(),
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = supplierId,
            VersionNo    = 1,
            Status       = "published",
            CreatedAt    = now,
            PublishedAt  = now,
            OutputMappingJson = outputMappingJson,
            OutputFormat      = outputFormat,
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revision.Id;
        await db.SaveChangesAsync();
        return revision;
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string RevisionOutputJson() => JsonSerializer.Serialize(new OutputMappingConfig
    {
        Header = { ["po"]   = new OutputFieldRule { OutputPath = "RevRef",  CanonicalField = "PoNumber" } },
        Lines  = { ["code"] = new OutputFieldRule { OutputPath = "RevCode", CanonicalField = "SupplierItemCode" } },
    }, CamelCase);

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

    private static string PreviewFormat(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var formatProp = ok.Value!.GetType().GetProperty("format");
        Assert.NotNull(formatProp);
        return (string)formatProp!.GetValue(ok.Value)!;
    }

    // ── Flag ON + pinned: preview == delivered bytes while live tables differ ──────────────────

    [Fact]
    public async Task FlagOn_Pinned_NoBodyNoOverride_RevisionOutputDrivesPreview_AndMatchesRealTransformBytes()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        await SeedPinnedRevisionAsync(db, orgId, supplierId, orderId, RevisionOutputJson());

        // A DIVERGENT live supplier-promoted mapping — must be ignored for a pinned order.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var controller = BuildController(db, orgId, Resolver(db, enabled: true));
        var preview = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.StartsWith("RevRef,RevCode", previewContent);       // the pinned revision's layout
        Assert.DoesNotContain("OrderRef,ItemCode", previewContent); // live promoted layout NOT used

        // DELIVERY side — the REAL transform with the SAME resolver.
        var (svc, captured) = BuildOrderService(db, Resolver(db, enabled: true));
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        var deliveredContent = Encoding.UTF8.GetString(captured()!);

        Assert.Equal(deliveredContent, previewContent); // preview == delivery, character for character
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionFormatOverridesRequestedFormat_AndMatchesRealTransform()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        // The revision pins the supplier contract to JSON with a usable output snapshot.
        await SeedPinnedRevisionAsync(db, orgId, supplierId, orderId, RevisionOutputJson(), outputFormat: "json");

        var controller = BuildController(db, orgId, Resolver(db, enabled: true));
        var preview = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        Assert.Equal("Json", PreviewFormat(preview)); // the revision's format governs, not ?format=csv
        var previewContent = PreviewContent(preview);
        using (var doc = JsonDocument.Parse(previewContent))
        {
            Assert.Equal("PO-RP-1", doc.RootElement.GetProperty("header").GetProperty("RevRef").GetString());
            Assert.Equal("SUP-1",   doc.RootElement.GetProperty("lines")[0].GetProperty("RevCode").GetString());
        }

        // DELIVERY side — same resolver: the artifact is JSON in the revision's layout. (Not
        // byte-compared — the native JSON builder embeds a generatedAt timestamp; the layout and
        // values are the deterministic parts.)
        var (svc, captured) = BuildOrderService(db, Resolver(db, enabled: true));
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        Assert.Equal("json", transform.Value!.Format);
        using (var delivered = JsonDocument.Parse(Encoding.UTF8.GetString(captured()!)))
        {
            Assert.Equal("PO-RP-1", delivered.RootElement.GetProperty("header").GetProperty("RevRef").GetString());
            Assert.Equal("SUP-1",   delivered.RootElement.GetProperty("lines")[0].GetProperty("RevCode").GetString());
        }
    }

    [Fact]
    public async Task FlagOn_Pinned_PerOrderOverrideStillBeatsRevision_AndMatchesRealTransformBytes()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        await SeedPinnedRevisionAsync(db, orgId, supplierId, orderId, RevisionOutputJson());

        // Per-order override — the HIGHEST-priority seam, unchanged by read-parity.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"]   = new OutputFieldRule { OutputPath = "PerOrderRef",  CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PerOrderCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        var controller = BuildController(db, orgId, Resolver(db, enabled: true));
        var preview = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.StartsWith("PerOrderRef,PerOrderCode", previewContent); // override wins
        Assert.DoesNotContain("RevRef", previewContent);               // revision layout NOT used

        var (svc, captured) = BuildOrderService(db, Resolver(db, enabled: true));
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        Assert.Equal(Encoding.UTF8.GetString(captured()!), previewContent);
    }

    // ── GOLDEN — flag OFF / unpinned: byte-identical to the pre-read-parity preview ────────────

    [Fact]
    public async Task FlagOff_Pinned_PreviewByteIdenticalToNoResolverPath_SupplierPromotedStillDrives()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        await SeedPinnedRevisionAsync(db, orgId, supplierId, orderId, RevisionOutputJson(), outputFormat: "json");

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        // Flag OFF — the divergent pinned revision (output AND format) must be invisible.
        var flagOff = await BuildController(db, orgId, Resolver(db, enabled: false))
            .PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        // No resolver at all (pre-read-parity construction) — the golden baseline.
        var noResolver = await BuildController(db, orgId, resolver: null)
            .PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        var flagOffContent = PreviewContent(flagOff);
        Assert.Equal("Csv", PreviewFormat(flagOff));                  // revision "json" NOT applied
        Assert.StartsWith("OrderRef,ItemCode", flagOffContent);       // 4A promoted layout, unchanged
        Assert.DoesNotContain("RevRef", flagOffContent);
        Assert.Equal(PreviewContent(noResolver), flagOffContent);     // byte-identical to the baseline
    }

    [Fact]
    public async Task FlagOn_Unpinned_PreviewByteIdenticalToNoResolverPath()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db); // NOT pinned

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var flagOn = await BuildController(db, orgId, Resolver(db, enabled: true))
            .PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);
        var noResolver = await BuildController(db, orgId, resolver: null)
            .PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        Assert.StartsWith("OrderRef,ItemCode", PreviewContent(flagOn));
        Assert.Equal(PreviewContent(noResolver), PreviewContent(flagOn));
    }

    // ── Flag ON + pinned + NULL revision output: the live promoted mapping is NOT consulted ────

    [Fact]
    public async Task FlagOn_Pinned_NullRevisionOutput_NoBodyNoOverride_Returns400_PromotedNotConsulted()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        // Backfilled-rev-1 shape: NO output mapping snapshotted → delivery uses the FIXED transformer.
        await SeedPinnedRevisionAsync(db, orgId, supplierId, orderId, outputMappingJson: null);

        // A live promoted mapping exists — but a PINNED order must NOT preview it (it would not
        // drive delivery), so the original body-required contract applies.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var controller = BuildController(db, orgId, Resolver(db, enabled: true));
        var result = await controller.PreviewMappingOverride(orderId, request: null, "csv", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
