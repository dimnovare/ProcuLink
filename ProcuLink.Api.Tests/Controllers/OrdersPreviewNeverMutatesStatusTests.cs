using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// POST /api/orders/{id}/mapping-override/preview is a DRY RUN: it must never mutate order status.
///
/// <para><b>Why this is pinned now.</b> A terminal transform failure is no longer reverted to
/// <c>ready</c> — it parks the order in the visible <c>transform_failed</c> status. That is right for
/// the automatic pipeline (the only caller of <c>IOrderService.TransformAsync</c> is
/// <c>TransformOrderJob</c>), but it would be badly wrong for the mapping editor, where the user is
/// ITERATING: the live editor calls this preview repeatedly, debounced as they type, and a
/// half-written template throws on nearly every keystroke. If the preview shared the transform's
/// failure handling, simply typing would flip the user's order into a scary failure status and light
/// up the ops-health tile.</para>
///
/// <para>It does not, and these pin that it stays that way: the preview resolves an effective entity
/// and calls the <c>ITransformService</c> DIRECTLY, never going through <c>TransformAsync</c>, so no
/// status write exists on the path at all. A broken template comes back as an inline
/// <c>{ ok:false, error }</c> at HTTP 200 for the editor to show — the order is untouched.</para>
/// </summary>
public class OrdersPreviewNeverMutatesStatusTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var resolver = new Mock<IEffectiveConnectionConfigResolver>();
        resolver
            .Setup(r => r.ResolveAsync(orgId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EffectiveConnectionConfig.Live);

        return new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService() },
            effectiveConfig: resolver.Object);
    }

    private static Guid SeedResolvedOrder(ProcuLinkDbContext db, Guid orgId, string status)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.Empty,
            PoNumber   = "PO-PREVIEW-1",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 1),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
            Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
        });
        db.SaveChanges();
        return orderId;
    }

    private static async Task<string> StatusOf(ProcuLinkDbContext db, Guid orderId) =>
        await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();

    [Fact]
    public async Task Preview_WithABrokenTemplate_ReturnsTheErrorInline_AndLeavesAReadyOrderReady()
    {
        // The mapping editor's normal state: the user is mid-keystroke and the template does not
        // compile. This must NOT flip a healthy 'ready' order into transform_failed.
        var orgId = Guid.NewGuid();
        await using var db = MakeDb();
        var controller = BuildController(db, orgId);
        var orderId    = SeedResolvedOrder(db, orgId, OrderStatusConstants.Ready);

        var result = await controller.PreviewMappingOverride(
            orderId,
            new OrderMappingOverride { OutputTemplate = "{{ for }}" },
            format: "csv", honorFormat: false, CancellationToken.None);

        // The editor shows the compile error inline at HTTP 200 — never a 4xx/5xx.
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("ok")!.GetValue(value).Should().Be(false);
        value.GetType().GetProperty("error")!.GetValue(value).Should().NotBeNull();

        (await StatusOf(db, orderId)).Should().Be(OrderStatusConstants.Ready);
    }

    [Fact]
    public async Task Preview_WithAWorkingOverride_LeavesTheStatusUntouched()
    {
        // Even a fully SUCCESSFUL preview must not advance the order — a dry run is not a transform,
        // and it produces no artifact to deliver.
        var orgId = Guid.NewGuid();
        await using var db = MakeDb();
        var controller = BuildController(db, orgId);
        var orderId    = SeedResolvedOrder(db, orgId, OrderStatusConstants.Ready);

        var result = await controller.PreviewMappingOverride(
            orderId,
            new OrderMappingOverride
            {
                Output = new OutputMappingConfig
                {
                    Header = new() { ["po"] = new OutputFieldRule { OutputPath = "PurchaseOrder", CanonicalField = "PoNumber" } },
                    Lines  = new() { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
                },
            },
            format: "csv", honorFormat: false, CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("content")!.GetValue(value).Should().NotBeNull();

        (await StatusOf(db, orderId)).Should().Be(OrderStatusConstants.Ready);
    }

    [Fact]
    public async Task Preview_OnATransformFailedOrder_DoesNotRescueOrReFailIt()
    {
        // The recovery loop: the order already failed, and the user is iterating on the fix in the
        // editor. Previewing must neither "heal" the status (hiding a real failure from ops health
        // before a real transform has succeeded) nor re-write it — it is read-only, full stop.
        var orgId = Guid.NewGuid();
        await using var db = MakeDb();
        var controller = BuildController(db, orgId);
        var orderId    = SeedResolvedOrder(db, orgId, OrderStatusConstants.TransformFailed);

        var result = await controller.PreviewMappingOverride(
            orderId,
            new OrderMappingOverride { OutputTemplate = "PO {{ PoNumber }}", OutputTemplateContentType = "text/plain" },
            format: "csv", honorFormat: false, CancellationToken.None);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("ok")!.GetValue(value).Should().Be(true);

        (await StatusOf(db, orderId)).Should().Be(OrderStatusConstants.TransformFailed);
    }
}
