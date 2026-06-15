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

    /// <summary>The real entity-based transformers, so structured-format previews can render.</summary>
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
            new OrderMappingOverrideService(db), // real service over the in-memory db
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            RealTransformers());
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

        // "edifact" is a real format but NOT an entity-based override format — must 400.
        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "edifact", ct: CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── preview now spans every entity-based output format ─────────────────────

    /// <summary>Seeds one resolved line + a supplier so the structured transforms can render.</summary>
    private static async Task SeedResolvedLineAndSupplierAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var order = await db.PurchaseOrders.FirstAsync(o => o.Id == orderId);
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Seeded Supplier OÜ" });
        order.SupplierId = supplierId;

        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B1", SupplierItemCode = "S1", Description = "Widget",
            Quantity = 2, UnitPrice = 5m, NeedsReview = false,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An override that overrides the PoNumber with a fixed value (works for every format).</summary>
    private static OrderMappingOverride PoNumberFixedValueOverride() => new()
    {
        Output = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "OVERRIDDEN-PO" } },
        },
    };

    [Theory]
    [InlineData("xml")]
    [InlineData("cxml")]
    [InlineData("ubl")]
    [InlineData("x12")]
    public async Task Preview_StructuredFormat_AppliesHeaderOverride(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        await SeedResolvedLineAndSupplierAsync(db, orgId, orderId);
        var ctrl = Build(db, orgId);

        var result = await ctrl.PreviewMappingOverride(orderId, PoNumberFixedValueOverride(), format, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // The preview content carries the overridden PO number and never the original.
        var content = ok.Value!.GetType().GetProperty("content")!.GetValue(ok.Value) as string;
        Assert.NotNull(content);
        Assert.Contains("OVERRIDDEN-PO", content);
        Assert.DoesNotContain("PO-1", content);

        // Non-mutating: no override persisted.
        var stored = await new OrderMappingOverrideService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.Null(stored);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("cxml")]
    [InlineData("ubl")]
    [InlineData("x12")]
    public async Task Preview_AllFormatsAccepted_ReturnOk(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        await SeedResolvedLineAndSupplierAsync(db, orgId, orderId);
        var ctrl = Build(db, orgId);

        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), format, ct: CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Preview_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, Guid.NewGuid());
        var ctrl    = Build(db, Guid.NewGuid()); // different org

        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "csv", ct: CancellationToken.None);
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
        var result = await ctrl.PreviewMappingOverride(orderId, ValidOverride(), "csv", ct: CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        // The dry-run must NOT have persisted an override or changed status.
        var stored = await new OrderMappingOverrideService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.Null(stored);
    }

    // ── Regression: no-usable-output override must NOT 500 (csv/json) ──────────
    //
    // The live preview harness POSTs an EMPTY body ('{}') for csv|json. That deserializes to an
    // OrderMappingOverride with a NULL Output. The native CSV/JSON builder (MappedTransformService.Build)
    // throws ArgumentException("Override has no output mapping config.") on a null Output, and the
    // preview endpoint only caught TransformValidationException → unhandled → HTTP 500. The xml/cxml/
    // ubl/x12 path was unaffected because EffectiveEntityResolver tolerates a null Output (identity
    // clone) and the harness routes those through the real transform endpoint.
    //
    // Expected behaviour: with no USABLE output config, the preview must fall back to the FIXED
    // transformer for that format (the same byte-identical output the order would actually deliver),
    // returning 200 with content — exactly like OrderTransformService gates on HasUsableOutput.

    /// <summary>Seeds a resolved line carrying realistic V5 data (qty, unit price, amount, tax rate).</summary>
    private static async Task SeedResolvedV5LineAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var order = await db.PurchaseOrders.FirstAsync(o => o.Id == orderId);
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "V5 Supplier OÜ" });
        order.SupplierId = supplierId;
        // V5 header enrichment present (totals) so the derived-total row-bag code runs with real data.
        order.SubTotal   = 250m;
        order.TaxTotal   = 50m;
        order.GrandTotal = 300m;
        order.PaymentTerms = "NET30";

        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "B1", SupplierItemCode = "S1", Description = "Widget",
            Quantity = 5m, Unit = "EA", UnitPrice = 50m,
            LineAmount = 250m, TaxRate = 0.20m, DeliveryDate = new DateOnly(2026, 7, 1),
            NeedsReview = false,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Preview_EmptyOverride_NoUsableOutput_ReturnsOk_NotThrow(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        await SeedResolvedV5LineAsync(db, orgId, orderId);
        var ctrl = Build(db, orgId);

        // Mirrors the live harness body '{}' — an override with a null Output config.
        var emptyOverride = new OrderMappingOverride();

        var result = await ctrl.PreviewMappingOverride(orderId, emptyOverride, format, ct: CancellationToken.None);

        // Must be a 200 with non-null content (the fixed-transform fallback), never an unhandled throw.
        var ok = Assert.IsType<OkObjectResult>(result);
        var content = ok.Value!.GetType().GetProperty("content")!.GetValue(ok.Value) as string;
        Assert.False(string.IsNullOrEmpty(content),
            $"{format} preview with an empty override must fall back to the fixed transform output.");

        // Non-mutating.
        var stored = await new OrderMappingOverrideService(db).GetAsync(orgId, orderId, CancellationToken.None);
        Assert.Null(stored);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Preview_CustomFieldsOnly_NoUsableOutput_ReturnsOk(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        await SeedResolvedV5LineAsync(db, orgId, orderId);
        var ctrl = Build(db, orgId);

        // Custom fields present but NO output mapping rules → still no usable output → fixed fallback.
        var customOnly = new OrderMappingOverride
        {
            CustomFields = { new CustomField { Key = "note", Label = "Note", Scope = "header", Value = "hello" } },
            Output = new OutputMappingConfig(), // present but EMPTY (Header.Count == 0 && Lines.Count == 0)
        };

        var result = await ctrl.PreviewMappingOverride(orderId, customOnly, format, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var content = ok.Value!.GetType().GetProperty("content")!.GetValue(ok.Value) as string;
        Assert.False(string.IsNullOrEmpty(content),
            $"{format} preview with custom-fields-only override must fall back to the fixed transform output.");
    }

    /// <summary>
    /// Regression: an override WITH a usable output config still drives the native CSV/JSON builder
    /// (the heart-piece path) — the no-output fallback must not swallow real overrides.
    /// </summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Preview_UsableOutput_StillUsesNativeOverrideBuilder(string format)
    {
        await using var db = NewDb();
        var orgId   = Guid.NewGuid();
        var orderId = await SeedOrderAsync(db, orgId);
        await SeedResolvedV5LineAsync(db, orgId, orderId);
        var ctrl = Build(db, orgId);

        // A usable output that emits a single distinctively-named column not present in the fixed shape.
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "DistinctRefColumn", CanonicalField = "PoNumber" } },
            },
        };

        var result = await ctrl.PreviewMappingOverride(orderId, ov, format, ct: CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var content = ok.Value!.GetType().GetProperty("content")!.GetValue(ok.Value) as string;
        Assert.NotNull(content);
        Assert.Contains("DistinctRefColumn", content!); // proves the native override builder ran
    }
}
