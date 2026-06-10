using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Group V8 controller coverage for <c>GET /api/orders/{id}/conformance</c>:
/// a resolved order produces a passing named-profile report; cross-tenant is 404;
/// an unresolved order is 422 (can't be transformed to a complete document); a
/// generic / unknown format is 400; the ?download=md variant returns text/markdown;
/// and the endpoint is NON-MUTATING (no status change, no artifact written).
/// Uses the REAL <see cref="ProcuLink.Transform.Conformance.ConformanceService"/> +
/// real entity transformers over an in-memory DbContext.
/// </summary>
public class OrdersControllerConformanceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ITransformService[] RealTransformers() => new ITransformService[]
    {
        new ProcuLink.Transform.Output.XmlTransformService(),
        new ProcuLink.Transform.Output.CsvTransformService(),
        new ProcuLink.Transform.Output.JsonTransformService(),
        new ProcuLink.Transform.Output.CxmlTransformService(),
        new ProcuLink.Transform.Output.UblOrderTransformService(),
        new ProcuLink.Transform.Output.X12TransformService(),
    };

    private static OrdersController Build(ProcuLinkDbContext db, Guid orgId)
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
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            RealTransformers());
    }

    private static async Task<Guid> SeedResolvedOrderAsync(ProcuLinkDbContext db, Guid orgId, bool needsReview = false)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-CONF-1",
            Currency   = "EUR",
            OrderDate  = new DateOnly(2026, 6, 9),
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "BUYER-1",
                    SupplierItemCode = needsReview ? null : "SUP-1",
                    Description = "Widget", Quantity = 5m, Unit = "EA", UnitPrice = 12.00m,
                    NeedsReview = needsReview,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    // ── 1. Resolved order → passing named-profile report ────────────────────────

    [Fact]
    public async Task GetConformance_ResolvedOrder_Cxml_ReturnsPassingReport()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ConformanceReportDto>().Subject;

        dto.OrderId.Should().Be(orderId);
        dto.Format.Should().Be("cxml");
        dto.Profile.Should().Be("CXml12OrderRequest");
        dto.ProfileName.Should().Be("cXML 1.2 OrderRequest");
        dto.OverallPass.Should().BeTrue();
        dto.ErrorCount.Should().Be(0);
        dto.Checks.Should().NotBeEmpty();
        dto.Checks.Should().OnlyContain(c => c.Passed);
        // The named profile reference is surfaced per check (standards-visibility rule).
        dto.Checks.Should().Contain(c => c.Code == "cxml.orderRequestHeader.orderID"
                                      && c.ProfileRef == "OrderRequestHeader/@orderID");
    }

    [Fact]
    public async Task GetConformance_ResolvedOrder_Ubl_ReturnsPassingReport()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: "ubl", download: null, default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConformanceReportDto>().Subject;
        dto.Profile.Should().Be("Ubl21Order");
        dto.OverallPass.Should().BeTrue();
    }

    // ── 2. Cross-tenant → 404 (org-scoped) ──────────────────────────────────────

    [Fact]
    public async Task GetConformance_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var ownerOrg = Guid.NewGuid();
        var orderId  = await SeedResolvedOrderAsync(db, ownerOrg);

        // A DIFFERENT org asks for the report.
        var ctrl = Build(db, Guid.NewGuid());

        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ── 3. Unresolved line → 422 ────────────────────────────────────────────────

    [Fact]
    public async Task GetConformance_UnresolvedOrder_Returns422()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId, needsReview: true);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    // ── 4. Unsupported / generic format → 400 ───────────────────────────────────

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("zzz")]
    public async Task GetConformance_GenericOrUnknownFormat_Returns400(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: format, download: null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetConformance_NoFormatAndNoSupplierConfig_Returns400()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: null, download: null, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── 5. Markdown download variant ────────────────────────────────────────────

    [Fact]
    public async Task GetConformance_DownloadMd_ReturnsMarkdownFile()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetConformance(orderId, format: "x12", download: "md", default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/markdown");
        file.FileDownloadName.Should().EndWith(".md");
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        text.Should().Contain("# Standards conformance report");
        text.Should().Contain("ANSI X12 850 Purchase Order");
    }

    // ── 6. Non-mutating: status unchanged, no artifact written ──────────────────

    [Fact]
    public async Task GetConformance_IsNonMutating()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedResolvedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var statusBefore  = (await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId)).Status;
        var artifactsBefore = await db.OutboundArtifacts.CountAsync();
        var updatedBefore = (await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId)).UpdatedAt;

        await ctrl.GetConformance(orderId, format: "cxml", download: null, default);

        var after = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        after.Status.Should().Be(statusBefore);
        after.UpdatedAt.Should().Be(updatedBefore);
        (await db.OutboundArtifacts.CountAsync()).Should().Be(artifactsBefore);
    }
}
