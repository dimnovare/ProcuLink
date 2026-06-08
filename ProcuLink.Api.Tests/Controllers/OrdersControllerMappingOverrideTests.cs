using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Controller coverage for the per-order mapping-override endpoints
/// (<c>GET/PUT /api/orders/{id}/mapping-override</c>, heart-piece-flex Phase 1):
/// manipulator validation (unknown type → 400), tenant isolation (cross-tenant order → 404),
/// and a GET that round-trips a prior PUT. Uses a REAL <see cref="OrderMappingOverrideService"/>
/// over an in-memory DbContext so the round-trip exercises the actual canonical_json read/write.
/// </summary>
public class OrdersControllerMappingOverrideTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

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
            new OrderMappingOverrideService(db), // real service over the in-memory db
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object);
    }

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-1",
            Currency   = "EUR",
            OrderDate  = new DateOnly(2026, 1, 1),
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private static OrderMappingOverride ValidOverride() => new()
    {
        Output = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber" } },
            Lines  =
            {
                ["code"] = new OutputFieldRule
                {
                    OutputPath = "Code", CanonicalField = "SupplierItemCode",
                    FieldManipulators = { new ManipulatorEntry { Type = "Trim", Params = { } } },
                },
            },
        },
    };

    [Fact]
    public async Task Put_UnknownManipulatorType_Returns400_AndDoesNotPersist()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var bad = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"] = new OutputFieldRule
                    {
                        OutputPath = "Code", CanonicalField = "SupplierItemCode",
                        FieldManipulators = { new ManipulatorEntry { Type = "DefinitelyNotAManipulator", Params = { } } },
                    },
                },
            },
        };

        var result = await ctrl.PutMappingOverride(orderId, bad, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        // Nothing was written.
        var stored = await new OrderMappingOverrideService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.Null(stored);
    }

    [Fact]
    public async Task Put_ManipulatorWithBadParams_Returns400()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        // Replace requires exactly 2 params — only one supplied → ctor throws → caught → 400.
        var bad = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header =
                {
                    ["po"] = new OutputFieldRule
                    {
                        OutputPath = "Ref", CanonicalField = "PoNumber",
                        FieldManipulators = { new ManipulatorEntry { Type = "Replace", Params = { "onlyone" } } },
                    },
                },
            },
        };

        var result = await ctrl.PutMappingOverride(orderId, bad, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Put_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var ownerOrg = Guid.NewGuid();
        var orderId  = await SeedOrderAsync(db, ownerOrg);

        // Controller is built for a DIFFERENT org.
        var ctrl = Build(db, Guid.NewGuid());

        var result = await ctrl.PutMappingOverride(orderId, ValidOverride(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var ownerOrg = Guid.NewGuid();
        var orderId  = await SeedOrderAsync(db, ownerOrg);

        var ctrl = Build(db, Guid.NewGuid());

        var result = await ctrl.GetMappingOverride(orderId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_OrderWithNoOverride_Returns200WithNullBody()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.GetMappingOverride(orderId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Null(ok.Value);
    }

    [Fact]
    public async Task Put_ThenGet_RoundTripsTheOverride()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var put = await ctrl.PutMappingOverride(orderId, ValidOverride(), CancellationToken.None);
        var putOk = Assert.IsType<OkObjectResult>(put);
        var putBody = Assert.IsType<OrderMappingOverride>(putOk.Value);
        Assert.True(putBody.Output!.Header.ContainsKey("po"));

        var get = await ctrl.GetMappingOverride(orderId, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(get);
        var getBody = Assert.IsType<OrderMappingOverride>(getOk.Value);

        Assert.Equal("Ref", getBody.Output!.Header["po"].OutputPath);
        Assert.Equal("PoNumber", getBody.Output.Header["po"].CanonicalField);
        Assert.Equal("Code", getBody.Output.Lines["code"].OutputPath);
        Assert.Single(getBody.Output.Lines["code"].FieldManipulators);
        Assert.Equal("Trim", getBody.Output.Lines["code"].FieldManipulators[0].Type);
    }

    // ── preview (Phase 3, dry-run) ────────────────────────────────────────────

    [Fact]
    public async Task Preview_UnsupportedFormat_Returns400()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        var ctrl    = Build(db, orgId);

        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "xml", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Preview_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, Guid.NewGuid());
        var ctrl    = Build(db, Guid.NewGuid()); // different org

        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "csv", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Preview_WithResolvedLine_ReturnsOk_NeverThrows()
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B1", SupplierItemCode = "S1", Description = "Widget",
            Quantity = 2, UnitPrice = 5m, NeedsReview = false,
        });
        await db.SaveChangesAsync();
        var ctrl = Build(db, orgId);

        // Dry-run: returns 200 with content (or a warning) — never writes, never 500s.
        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "csv", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        // The dry-run must NOT have persisted an override or changed status.
        var stored = await new OrderMappingOverrideService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.Null(stored);
    }
}
