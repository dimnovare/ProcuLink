using System.Text.Json;
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
/// Controller + service integration tests for
/// <c>POST /api/orders/{id}/mapping-override/promote</c>.
///
/// Each test uses a REAL <see cref="PromoteMappingService"/> wired over a real
/// <see cref="PoMappingService"/> over an in-memory DbContext, so the round-trip
/// exercises the actual canonical_json read → FieldMappingEntry translation →
/// PoMappingConfig upsert → verification read.
///
/// Covered:
/// <list type="number">
///   <item>SourceMap header + line fields translate to the correct PoMappingConfig entries.</item>
///   <item>Idempotent re-promote overwrites without duplicating entries.</item>
///   <item>Org-scoping — a cross-tenant order id returns 404.</item>
///   <item>Unknown order id returns 404.</item>
///   <item>An order with no SourceMap returns 200 with zero promoted fields (no-op).</item>
///   <item>Manipulators are carried through verbatim.</item>
/// </list>
/// </summary>
public class OrdersControllerPromoteMappingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Builds the controller with REAL PromoteMappingService + PoMappingService so the full
    /// translation and persistence are exercised end-to-end (no mocks for those services).
    /// </summary>
    private static (OrdersController Ctrl, PoMappingService PoMapping) Build(
        ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var poMappingService = new PoMappingService(db);
        var promoteService   = new PromoteMappingService(db, poMappingService);

        var ctrl = new OrdersController(
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
            promoteService,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object);

        return (ctrl, poMappingService);
    }

    /// <summary>
    /// Seeds an order whose canonical_json contains a <c>mappingOverride.sourceMap</c> entry.
    /// Returns (orderId, supplierId).
    /// </summary>
    private static async Task<(Guid OrderId, Guid SupplierId)> SeedOrderWithSourceMapAsync(
        ProcuLinkDbContext db,
        Guid orgId,
        OrderMappingOverride @override)
    {
        var orderId    = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Serialise the override into canonical_json under the "mappingOverride" key,
        // matching exactly what OrderMappingOverrideService.UpsertAsync does.
        var overrideJson = JsonSerializer.Serialize(@override, new JsonSerializerOptions
        {
            PropertyNamingPolicy          = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition        = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        var canonicalJson = JsonDocument.Parse($@"{{""mappingOverride"":{overrideJson}}}");

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id           = orderId,
            OrgId        = orgId,
            SupplierId   = supplierId,
            PoNumber     = "PO-PROMOTE-1",
            Currency     = "EUR",
            OrderDate    = new DateOnly(2026, 1, 1),
            Status       = "ready",
            CanonicalJson = canonicalJson,
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orderId, supplierId);
    }

    private static async Task<Guid> SeedOrderNoOverrideAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var orderId    = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-NO-OVERRIDE",
            Currency   = "EUR",
            OrderDate  = new DateOnly(2026, 1, 1),
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    // ── Test 1: SourceMap entries translate correctly ─────────────────────────

    [Fact]
    public async Task Promote_SourceMapWithHeaderAndLineFields_PersistsCorrectPoMappingConfig()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, poSvc)   = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                // Header field: SourceToken → ExternalField
                ["PoNumber"] = new SourceFieldRule { SourceToken = "PO_Number" },
                // Header field: FixedValue only
                ["Currency"] = new SourceFieldRule { FixedValue = "EUR" },
                // Line field: SourceToken → ExternalField, with a manipulator
                ["BuyerItemCode"] = new SourceFieldRule
                {
                    SourceToken   = "item_code",
                    Manipulators  = new List<ManipulatorEntry>
                    {
                        new ManipulatorEntry { Type = "Trim", Params = new List<string>() },
                    },
                },
                // Line field: FixedValue
                ["Unit"] = new SourceFieldRule { FixedValue = "EA" },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);

        // Verify the persisted PoMappingConfig via a real round-trip read.
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);

        // Header: PoNumber
        Assert.True(saved.Header.ContainsKey("PoNumber"),
            "PoNumber should be in Header");
        Assert.Equal("PO_Number", saved.Header["PoNumber"].ExternalField);
        Assert.Null(saved.Header["PoNumber"].FixedValue);
        Assert.Empty(saved.Header["PoNumber"].FieldManipulators);

        // Header: Currency (fixed value)
        Assert.True(saved.Header.ContainsKey("Currency"),
            "Currency should be in Header");
        Assert.Null(saved.Header["Currency"].ExternalField);
        Assert.Equal("EUR", saved.Header["Currency"].FixedValue);

        // Lines: BuyerItemCode with manipulator
        Assert.True(saved.Lines.ContainsKey("BuyerItemCode"),
            "BuyerItemCode should be in Lines");
        Assert.Equal("item_code", saved.Lines["BuyerItemCode"].ExternalField);
        var manip = Assert.Single(saved.Lines["BuyerItemCode"].FieldManipulators);
        Assert.Equal("Trim", manip.Type);

        // Lines: Unit (fixed value)
        Assert.True(saved.Lines.ContainsKey("Unit"),
            "Unit should be in Lines");
        Assert.Equal("EA", saved.Lines["Unit"].FixedValue);
    }

    // ── Test 2: summary DTO shape ─────────────────────────────────────────────

    [Fact]
    public async Task Promote_SuccessfulPromotion_ReturnsSummaryWithCounts()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, _)       = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"]     = new SourceFieldRule { SourceToken = "PO_Number" },
                ["OrderDate"]    = new SourceFieldRule { SourceToken = "order_date" },
                ["BuyerItemCode"] = new SourceFieldRule { SourceToken = "buyer_sku" },
                ["Quantity"]     = new SourceFieldRule { SourceToken = "qty" },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        // Serialise to JSON to inspect the anonymous DTO shape the frontend receives.
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(supplierId.ToString(), doc.RootElement.GetProperty("supplierId").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("lineFieldsPromoted").GetInt32());
        // schemaFingerprintHash is null (no fingerprint recorded on the seeded order).
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("schemaFingerprintHash").ValueKind);
    }

    // ── Test 3: idempotent re-promote ─────────────────────────────────────────

    [Fact]
    public async Task Promote_CalledTwice_IsIdempotent_NoExtraEntries()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, poSvc)   = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "PO_Number" },
                ["Description"] = new SourceFieldRule { SourceToken = "desc" },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        // First promote.
        var r1 = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r1);

        // Second promote (same override, same order).
        var r2 = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(r2);

        // The persisted config should have exactly 1 Header entry and 1 Lines entry.
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        Assert.Single(saved.Header);
        Assert.Single(saved.Lines);
    }

    // ── Test 4: promote merges, not replaces ──────────────────────────────────

    [Fact]
    public async Task Promote_ExistingMappingHasOtherFields_PreservesUnrelatedEntries()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, poSvc)   = Build(db, orgId);

        // Seed an order for the supplier.
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var overrideForOrder = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "PO_No" },
            },
        };
        var overrideJson = JsonSerializer.Serialize(overrideForOrder, new JsonSerializerOptions
        {
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-MERGE",
            Currency      = "EUR",
            OrderDate     = new DateOnly(2026, 1, 1),
            Status        = "ready",
            CanonicalJson = JsonDocument.Parse($@"{{""mappingOverride"":{overrideJson}}}"),
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Pre-populate the supplier mapping with an existing entry for a different field.
        await poSvc.UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Header = new Dictionary<string, FieldMappingEntry>
            {
                ["BuyerName"] = new FieldMappingEntry { ExternalField = "buyer" },
            },
        });

        // Promote should ADD PoNumber without removing BuyerName.
        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        Assert.True(saved.Header.ContainsKey("BuyerName"), "BuyerName must be preserved");
        Assert.True(saved.Header.ContainsKey("PoNumber"),  "PoNumber must be added");
        Assert.Equal(2, saved.Header.Count);
    }

    // ── Test 5: org-scoping ───────────────────────────────────────────────────

    [Fact]
    public async Task Promote_CrossTenantOrder_Returns404()
    {
        await using var db = NewDb();
        var ownerOrg       = Guid.NewGuid();

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "PO_Number" },
            },
        };
        var (orderId, _) = await SeedOrderWithSourceMapAsync(db, ownerOrg, @override);

        // Build controller for a DIFFERENT org.
        var (ctrl, _) = Build(db, Guid.NewGuid());

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Test 6: unknown order id returns 404 ─────────────────────────────────

    [Fact]
    public async Task Promote_UnknownOrderId_Returns404()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, _)      = Build(db, orgId);

        var result = await ctrl.PromoteMappingOverride(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Test 7: no SourceMap → 200 with zero fields ───────────────────────────

    [Fact]
    public async Task Promote_OrderWithNoSourceMap_Returns200_ZeroFieldsPromoted()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, poSvc)   = Build(db, orgId);

        var orderId = await SeedOrderNoOverrideAsync(db, orgId);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("lineFieldsPromoted").GetInt32());
    }

    // ── Test 8: unknown canonical field names are silently skipped ─────────────

    [Fact]
    public async Task Promote_UnknownCanonicalFieldNames_SkippedSilently()
    {
        await using var db  = NewDb();
        var orgId           = Guid.NewGuid();
        var (ctrl, poSvc)   = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"]          = new SourceFieldRule { SourceToken = "po" },
                // Not a known canonical field — must be silently skipped.
                ["CustomField_123"]   = new SourceFieldRule { SourceToken = "custom" },
                ["AnotherUnknown"]    = new SourceFieldRule { FixedValue  = "literal" },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        // Only PoNumber is a recognised header field.
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        Assert.Single(saved.Header);
        Assert.True(saved.Header.ContainsKey("PoNumber"));
        Assert.Empty(saved.Lines);

        // The JSON summary counts only promoted fields (unknown ones do NOT count).
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("lineFieldsPromoted").GetInt32());
    }

    // ── Test 9: the OUTPUT side is promoted, persisted, and counted ────────────────

    [Fact]
    public async Task Promote_OutputMapping_PersistsAndCounts()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, poSvc)  = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "po" },
            },
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>
                {
                    ["po"] = new OutputFieldRule { OutputPath = "PurchaseOrder", CanonicalField = "PoNumber" },
                },
                Lines = new Dictionary<string, OutputFieldRule>
                {
                    ["sku"] = new OutputFieldRule { OutputPath = "SKU",      CanonicalField = "SupplierItemCode" },
                    ["qty"] = new OutputFieldRule { OutputPath = "Quantity", CanonicalField = "Quantity" },
                },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        // The supplier config now carries the promoted output mapping (round-trips through JSONB).
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        Assert.NotNull(saved!.Output);
        Assert.Single(saved.Output!.Header);
        Assert.Equal(2, saved.Output.Lines.Count);
        Assert.Equal("PurchaseOrder", saved.Output.Header["po"].OutputPath);
        Assert.Equal("SKU",           saved.Output.Lines["sku"].OutputPath);

        // The response reports the output counts + a non-empty message + not nothingToPromote.
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("outputHeaderFieldsPromoted").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("outputLineFieldsPromoted").GetInt32());
        // total = 1 inbound header + 1 output header + 2 output lines = 4
        Assert.Equal(4, doc.RootElement.GetProperty("totalFieldsPromoted").GetInt32());
        Assert.False(doc.RootElement.GetProperty("nothingToPromote").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    // ── Test 10: output-only override (no SourceMap) still promotes + counts ────────

    [Fact]
    public async Task Promote_OutputOnly_NoSourceMap_StillPersists()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, poSvc)  = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>
                {
                    ["po"] = new OutputFieldRule { OutputPath = "OrderNo", CanonicalField = "PoNumber" },
                },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        Assert.NotNull(saved!.Output);
        Assert.Single(saved.Output!.Header);

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("outputHeaderFieldsPromoted").GetInt32());
        Assert.False(doc.RootElement.GetProperty("nothingToPromote").GetBoolean());
    }

    // ── Test 11: nothing to promote → clear message, supplier mapping untouched ────

    [Fact]
    public async Task Promote_NoSourceMapNoOutput_ReportsNothingToPromote_DoesNotPersist()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, poSvc)  = Build(db, orgId);

        var orderId = await SeedOrderNoOverrideAsync(db, orgId);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("nothingToPromote").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("totalFieldsPromoted").GetInt32());
        var msg = doc.RootElement.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.Contains("Nothing to save", msg);

        // No supplier mapping row was written for this supplier (genuine no-op, not a silent save).
        var supplierId = await db.PurchaseOrders.Where(o => o.Id == orderId).Select(o => o.SupplierId).FirstAsync();
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.Null(saved);
    }

    // ── Test 12: override with ONLY unknown SourceMap fields → nothingToPromote ─────

    [Fact]
    public async Task Promote_OnlyUnknownSourceFields_NoOutput_ReportsNothingToPromote()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, poSvc)  = Build(db, orgId);

        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["TotallyUnknown"] = new SourceFieldRule { SourceToken = "x" },
            },
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("nothingToPromote").GetBoolean());

        // Nothing was persisted.
        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.Null(saved);
    }

    // ── Test 13: empty output config (zero rules) does NOT clobber an existing one ──

    [Fact]
    public async Task Promote_EmptyOutputConfig_PreservesExistingSupplierOutput()
    {
        await using var db = NewDb();
        var orgId          = Guid.NewGuid();
        var (ctrl, poSvc)  = Build(db, orgId);

        // SourceMap with a known field so the promote isn't a no-op, plus an EMPTY output config.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "po" },
            },
            Output = new OutputMappingConfig(), // zero header + zero line rules
        };

        var (orderId, supplierId) = await SeedOrderWithSourceMapAsync(db, orgId, @override);

        // Pre-seed an existing supplier output mapping that must NOT be wiped by the empty one.
        await poSvc.UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>
                {
                    ["keep"] = new OutputFieldRule { OutputPath = "KeepMe", CanonicalField = "PoNumber" },
                },
            },
        });

        var result = await ctrl.PromoteMappingOverride(orderId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);

        var saved = await poSvc.GetAsync(orgId, supplierId);
        Assert.NotNull(saved);
        // Inbound PoNumber was added.
        Assert.True(saved!.Header.ContainsKey("PoNumber"));
        // Existing output mapping preserved (empty incoming output must not clobber it).
        Assert.NotNull(saved.Output);
        Assert.True(saved.Output!.Header.ContainsKey("keep"));

        // Reported counts: 1 inbound header, 0 output.
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("headerFieldsPromoted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("outputHeaderFieldsPromoted").GetInt32());
        Assert.False(doc.RootElement.GetProperty("nothingToPromote").GetBoolean());
    }
}
