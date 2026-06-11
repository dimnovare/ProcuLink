using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Read-parity (launch batch 7 follow-up) — <c>GET /api/orders/{id}/conformance</c> must report on
/// the document delivery would ACTUALLY produce for a PINNED order under the
/// <c>Connections:RevisionAuthority</c> flag: the revision's snapshotted output format governs
/// (outranking the explicit <c>?format=</c>, exactly as the transform swaps it), and the document is
/// rendered with the transform's effective priority (per-order override → pinned revision output →
/// fixed). GOLDEN guard: flag OFF / unpinned reports stay byte-identical to today even when a
/// divergent revision exists.
/// </summary>
public class OrdersConformanceRevisionAuthorityTests
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
        new ProcuLink.Transform.Output.XmlTransformService(),
        new ProcuLink.Transform.Output.CsvTransformService(),
        new ProcuLink.Transform.Output.JsonTransformService(),
        new ProcuLink.Transform.Output.CxmlTransformService(),
        new ProcuLink.Transform.Output.UblOrderTransformService(),
        new ProcuLink.Transform.Output.X12TransformService(),
    };

    private static OrdersController Build(ProcuLinkDbContext db, Guid orgId, IEffectiveConnectionConfigResolver? resolver)
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
            new Mock<IOrderMappingOverrideService>().Object,
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            RealTransformers(),
            effectiveConfig: resolver);
    }

    private static async Task<(Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-CONF-RP",
            Currency   = "EUR",
            OrderDate  = new DateOnly(2026, 6, 11),
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "BUYER-1",
                    SupplierItemCode = "SUP-1",
                    Description = "Widget", Quantity = 5m, Unit = "EA", UnitPrice = 12.00m,
                    NeedsReview = false,
                },
            },
        });
        await db.SaveChangesAsync();
        return (supplierId, orderId);
    }

    /// <summary>Seeds a published connection revision; optionally pins the order to it.</summary>
    private static async Task<SupplierConnectionRevision> SeedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string? outputMappingJson, string? outputFormat, Guid? pinOrderId = null)
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

        if (pinOrderId is not null)
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == pinOrderId.Value);
            order.ConnectionRevisionId = revision.Id;
            await db.SaveChangesAsync();
        }
        return revision;
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A revision output snapshot that BLANKS PoNumber — under read-parity the delivered cXML
    /// has an empty @orderID, which the named profile flags. Sharp proof the EFFECTIVE output is checked.</summary>
    private static string BlankPoNumberOutputJson() => JsonSerializer.Serialize(new OutputMappingConfig
    {
        Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "" } },
    }, CamelCase);

    private static ConformanceReportDto Report(IActionResult result) =>
        Assert.IsType<ConformanceReportDto>(Assert.IsType<OkObjectResult>(result).Value);

    // ── Flag ON + pinned: revision format + revision output govern the report ──────────────────

    [Fact]
    public async Task FlagOn_Pinned_RevisionFormatGoverns_OverExplicitQuery()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId);
        // The pinned contract delivers UBL (no output snapshot → fixed UBL transformer).
        await SeedRevisionAsync(db, orgId, supplierId, outputMappingJson: null, outputFormat: "ubl", pinOrderId: orderId);

        var ctrl = Build(db, orgId, Resolver(db, enabled: true));
        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var dto = Report(result);
        Assert.Equal("ubl", dto.Format);          // the revision's format, NOT the requested cxml
        Assert.Equal("Ubl21Order", dto.Profile);  // validated against the UBL profile
        Assert.True(dto.OverallPass);
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionNonNamedFormat_Returns400_Honestly()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId);
        // The pinned contract delivers CSV — no named standards profile exists for what would go out.
        await SeedRevisionAsync(db, orgId, supplierId, outputMappingJson: null, outputFormat: "csv", pinOrderId: orderId);

        var ctrl = Build(db, orgId, Resolver(db, enabled: true));
        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = (string)bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)!;
        Assert.Contains("pinned", error); // the message explains the pinned contract drives the format
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionOutputDrivesDocument_FailingCheckSurfacesHonestly()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId);
        // The pinned revision's output snapshot blanks PoNumber → the delivered cXML has an empty
        // @orderID. The report must validate THAT document, not the raw-entity render.
        await SeedRevisionAsync(db, orgId, supplierId, BlankPoNumberOutputJson(), outputFormat: "cxml", pinOrderId: orderId);

        var ctrl = Build(db, orgId, Resolver(db, enabled: true));
        var result = await ctrl.GetConformance(orderId, format: null, download: null, default);

        var dto = Report(result);
        Assert.Equal("cxml", dto.Format);
        Assert.False(dto.OverallPass); // the revision-driven document genuinely fails the profile
        Assert.Contains(dto.Checks, c => c.Code == "cxml.orderRequestHeader.orderID" && !c.Passed);
    }

    // ── GOLDEN — flag OFF / unpinned: byte-identical reports even when a divergent revision exists ──

    [Fact]
    public async Task FlagOff_Pinned_ReportIdenticalToToday_RevisionIgnored()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId);
        // Same divergent revision as the failing test above — but the flag is OFF.
        await SeedRevisionAsync(db, orgId, supplierId, BlankPoNumberOutputJson(), outputFormat: "csv", pinOrderId: orderId);

        var ctrl = Build(db, orgId, Resolver(db, enabled: false));
        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var dto = Report(result);
        Assert.Equal("cxml", dto.Format);   // the explicit query wins (revision "csv" NOT applied)
        Assert.True(dto.OverallPass);       // raw-entity render — the blanking snapshot NOT applied
        Assert.Contains(dto.Checks, c => c.Code == "cxml.orderRequestHeader.orderID" && c.Passed);
    }

    [Fact]
    public async Task FlagOn_Unpinned_ReportIdenticalToToday()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId); // NOT pinned
        await SeedRevisionAsync(db, orgId, supplierId, BlankPoNumberOutputJson(), outputFormat: "csv");

        var ctrl = Build(db, orgId, Resolver(db, enabled: true));
        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var dto = Report(result);
        Assert.Equal("cxml", dto.Format);
        Assert.True(dto.OverallPass);
    }

    // ── Flag ON + pinned stays NON-MUTATING (read-only parity must not write anything) ──────────

    [Fact]
    public async Task FlagOn_Pinned_IsNonMutating()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var (supplierId, orderId) = await SeedResolvedOrderAsync(db, orgId);
        await SeedRevisionAsync(db, orgId, supplierId, BlankPoNumberOutputJson(), outputFormat: "cxml", pinOrderId: orderId);

        var before = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        var artifactsBefore = await db.OutboundArtifacts.CountAsync();

        var ctrl = Build(db, orgId, Resolver(db, enabled: true));
        await ctrl.GetConformance(orderId, format: null, download: null, default);

        var after = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(artifactsBefore, await db.OutboundArtifacts.CountAsync());
    }
}
