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
/// WP-12 defect D8 — the workshop preview must equal the delivered bytes.
///
/// <para>The preview's config fallback builds a synthetic override carrying <c>Output</c> only, so an
/// order whose supplier (or pinned revision) carries a promoted <c>OutputTree</c> previewed the FIXED
/// document and delivered the tree-rendered one — contradicting the endpoint's own documented promise
/// that the preview equals delivery. Every assertion here compares the preview STRING against the
/// REAL <c>OrderService.TransformAsync</c> artifact bytes for the same order, so it cannot pass by
/// re-implementing the expectation on both sides.</para>
/// </summary>
public class OrdersPreviewPromotedOutputTreeParityTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static ITransformService[] RealTransformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    private static IEffectiveConnectionConfigResolver Authority(ProcuLinkDbContext db) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = "true",
            })
            .Build());

    private static OrdersController BuildController(
        ProcuLinkDbContext db, Guid orgId, IEffectiveConnectionConfigResolver? effectiveConfig = null)
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
            RealTransformers(),
            effectiveConfig: effectiveConfig);
    }

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) BuildOrderService(
        ProcuLinkDbContext db, IEffectiveConnectionConfigResolver? effectiveConfig = null)
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
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: effectiveConfig);

        return (svc, () => captured);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Preview Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-PREVIEW-1",
            BuyerName = "Preview Buyer", OrderDate = new DateOnly(2026, 7, 29), Currency = "EUR",
            Status = "ready", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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

    private static OutputNodeTemplate JsonTree(string fieldName) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf(fieldName, new OutputFieldRule { OutputPath = fieldName, CanonicalField = "PoNumber" })),
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

    [Fact]
    public async Task Preview_SupplierPromotedTree_MatchesTheRealTransformBytes()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = JsonTree("promotedLayout") }, CancellationToken.None);

        // PREVIEW — no body, no per-order override: the promoted tree is what delivery will use.
        var preview = await BuildController(db, orgId)
            .PreviewMappingOverride(orderId, request: null, "csv", ct: CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.Contains("promotedLayout", previewContent);

        // DELIVERY — the REAL transform for the same order.
        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);

        Assert.Equal(Encoding.UTF8.GetString(captured()!), previewContent);
    }

    [Fact]
    public async Task Preview_PinnedRevisionTree_MatchesTheRealTransformBytes()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedAsync(db);

        var now          = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revisionId, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", CreatedAt = now, PublishedAt = now,
            InputMappingJson = JsonSerializer.Serialize(
                new PoMappingConfig { OutputTree = JsonTree("pinnedLayout") }, CamelCase),
            OutputFormat = "json",
        });
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();

        var preview = await BuildController(db, orgId, Authority(db))
            .PreviewMappingOverride(orderId, request: null, "csv", ct: CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.Contains("pinnedLayout", previewContent);

        var (svc, captured) = BuildOrderService(db, Authority(db));
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);

        Assert.Equal(Encoding.UTF8.GetString(captured()!), previewContent);
    }

    [Fact]
    public async Task Preview_PerOrderOverride_StillOutranksTheSupplierPromotedTree()
    {
        // Assert the DIFFERENCE: the preview's new tree fallback must not overrule a per-order decision.
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = JsonTree("promotedLayout") }, CancellationToken.None);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PerOrderRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var preview = await BuildController(db, orgId)
            .PreviewMappingOverride(orderId, request: null, "csv", ct: CancellationToken.None);
        var previewContent = PreviewContent(preview);

        Assert.StartsWith("PerOrderRef", previewContent);
        Assert.DoesNotContain("promotedLayout", previewContent);

        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        Assert.Equal(Encoding.UTF8.GetString(captured()!), previewContent);
    }

    [Fact]
    public async Task Preview_PromotedCxmlEnvelopeTree_DoesNotHijackTheCsvPreview()
    {
        // A cXML/X12 tree carries identity, never a layout — it must not divert the preview either.
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedAsync(db);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            OutputTree = new OutputNodeTemplate
            {
                Format   = OutputFormat.CXml,
                Root     = OutputNode.Obj("cXML"),
                Envelope = new EnvelopeConfig { Cxml = new CxmlEnvelope { FromIdentity = "SUPPLIER-IDENTITY" } },
            },
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PromotedRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var preview = await BuildController(db, orgId)
            .PreviewMappingOverride(orderId, request: null, "csv", ct: CancellationToken.None);
        var previewContent = PreviewContent(preview);
        Assert.StartsWith("PromotedRef", previewContent);

        var (svc, captured) = BuildOrderService(db);
        var transform = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(transform.IsSuccess, transform.Error);
        Assert.Equal(Encoding.UTF8.GetString(captured()!), previewContent);
    }
}
