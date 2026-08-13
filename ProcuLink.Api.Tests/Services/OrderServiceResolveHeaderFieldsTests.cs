using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Verifies that <see cref="OrderService.ResolveAsync"/> persists optional header-field
/// corrections (order date / buyer name / currency) submitted from the review screen.
///
/// Critical invariant: BuyerName is denormalised — it lives in BOTH the
/// <c>purchase_orders.buyer_name</c> column AND canonical_json. A resolve edit must update
/// both so the GET read path (column-first, json-fallback) stays consistent. OrderDate and
/// Currency are sourced from columns by the read path; we still mirror them into
/// canonical_json for consistency. PO number + supplier are NOT writable here.
/// </summary>
public class OrderServiceResolveHeaderFieldsTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService Build(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.UpsertAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<MappingSource>(), It.IsAny<float?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var aiMappings = new Mock<IAiMappingService>();

        var poMappings = new Mock<IPoMappingService>();

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser() }),
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>
    /// Seed a single pending-review order with one unresolved line.
    /// CanonicalJson starts populated to mirror the real sync-path shape so the merge
    /// helper has existing properties to preserve.
    /// </summary>
    private static async Task<(Guid orgId, Guid orderId)> SeedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Acme Supplier", CreatedAt = DateTime.UtcNow,
        });

        var canonical = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            source    = "csv_upload",
            buyerName = "Old Buyer Ltd",
            poNumber  = "PO-1001",
            orderDate = "2026-01-01",
            currency  = "EUR",
        }));

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-1001",
            BuyerName     = "Old Buyer Ltd",
            OrderDate     = new DateOnly(2026, 1, 1),
            Currency      = "EUR",
            Status        = "pending_review",
            CanonicalJson = canonical,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUYER-1", Description = "Widget",
                    Quantity = 2, Unit = "PCS", UnitPrice = 9.99m,
                    NeedsReview = true, Confidence = 0f,
                },
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static async Task<PurchaseOrderEntity> ReloadAsync(ProcuLinkDbContext db, Guid orderId) =>
        await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == orderId);

    private static (string? buyerName, string? orderDate, string? currency) ReadCanonical(JsonDocument? doc)
    {
        if (doc is null) return (null, null, null);
        var root = doc.RootElement;
        string? Get(string key) => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
        return (Get("buyerName"), Get("orderDate"), Get("currency"));
    }

    // ── Happy path: header edits persist to BOTH column and canonical_json ──────────

    [Fact]
    public async Task ResolveAsync_PersistsAllHeaderFields_ToColumnsAndCanonicalJson()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var header = new ResolveHeaderFields(
            OrderDate: new DateOnly(2026, 3, 15),
            BuyerName: "New Buyer GmbH",
            Currency:  "USD");

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            header);

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);

        // Columns (what the GET read path actually sources OrderDate/Currency from,
        // and BuyerName column-first).
        Assert.Equal(new DateOnly(2026, 3, 15), reloaded.OrderDate);
        Assert.Equal("USD", reloaded.Currency);
        Assert.Equal("New Buyer GmbH", reloaded.BuyerName);

        // canonical_json mirror stays consistent with the columns.
        var (jsonBuyer, jsonDate, jsonCurrency) = ReadCanonical(reloaded.CanonicalJson);
        Assert.Equal("New Buyer GmbH", jsonBuyer);
        Assert.Equal("2026-03-15", jsonDate);
        Assert.Equal("USD", jsonCurrency);

        // Other canonical properties are preserved, not clobbered.
        Assert.True(reloaded.CanonicalJson!.RootElement.TryGetProperty("poNumber", out var po));
        Assert.Equal("PO-1001", po.GetString());
        Assert.True(reloaded.CanonicalJson!.RootElement.TryGetProperty("source", out var src));
        Assert.Equal("csv_upload", src.GetString());

        // PO number column is untouched (read-only).
        Assert.Equal("PO-1001", reloaded.PoNumber);
    }

    // ── Coexistence: header edits + line resolutions in the same request ───────────

    [Fact]
    public async Task ResolveAsync_HeaderEditsCoexistWithLineResolutions()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var header = new ResolveHeaderFields(BuyerName: "Coexist Buyer");

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            header);

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);

        // Line resolution applied.
        var line = Assert.Single(reloaded.Lines);
        Assert.Equal("SUP-1", line.SupplierItemCode);
        Assert.False(line.NeedsReview);

        // Header edit applied to both stores.
        Assert.Equal("Coexist Buyer", reloaded.BuyerName);
        var (jsonBuyer, _, _) = ReadCanonical(reloaded.CanonicalJson);
        Assert.Equal("Coexist Buyer", jsonBuyer);

        // All lines resolved → status advances to ready.
        Assert.Equal("ready", reloaded.Status);
    }

    // ── No-change: null header fields leave the order unchanged ────────────────────

    [Fact]
    public async Task ResolveAsync_AllNullHeaderFields_LeavesHeaderUnchanged()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields()); // all null

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);

        // Header columns unchanged.
        Assert.Equal(new DateOnly(2026, 1, 1), reloaded.OrderDate);
        Assert.Equal("EUR", reloaded.Currency);
        Assert.Equal("Old Buyer Ltd", reloaded.BuyerName);

        // Canonical unchanged too.
        var (jsonBuyer, jsonDate, jsonCurrency) = ReadCanonical(reloaded.CanonicalJson);
        Assert.Equal("Old Buyer Ltd", jsonBuyer);
        Assert.Equal("2026-01-01", jsonDate);
        Assert.Equal("EUR", jsonCurrency);

        // Line still got resolved (the resolve itself happened).
        Assert.Equal("SUP-1", Assert.Single(reloaded.Lines).SupplierItemCode);
    }

    // ── No header argument at all (null) behaves like a lines-only resolve ─────────

    [Fact]
    public async Task ResolveAsync_NullHeaderArgument_IsLinesOnlyResolve()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            header: null);

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);
        Assert.Equal("Old Buyer Ltd", reloaded.BuyerName);
        Assert.Equal("EUR", reloaded.Currency);
        Assert.Equal(new DateOnly(2026, 1, 1), reloaded.OrderDate);
        Assert.Equal("SUP-1", Assert.Single(reloaded.Lines).SupplierItemCode);
    }

    // ── Partial edit: only one field changes; the others are preserved ─────────────

    [Fact]
    public async Task ResolveAsync_OnlyCurrency_LeavesOtherHeaderFieldsIntact()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields(Currency: "gbp")); // lowercase → stored upper

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);
        Assert.Equal("GBP", reloaded.Currency);
        var (jsonBuyer, jsonDate, jsonCurrency) = ReadCanonical(reloaded.CanonicalJson);
        Assert.Equal("GBP", jsonCurrency);

        // Untouched.
        Assert.Equal("Old Buyer Ltd", reloaded.BuyerName);
        Assert.Equal(new DateOnly(2026, 1, 1), reloaded.OrderDate);
        Assert.Equal("Old Buyer Ltd", jsonBuyer);
        Assert.Equal("2026-01-01", jsonDate);
    }

    // ── Whitespace-only buyer name is treated as no-change ────────────────────────

    [Fact]
    public async Task ResolveAsync_WhitespaceBuyerName_IsNoChange()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields(BuyerName: "   "));

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);
        Assert.Equal("Old Buyer Ltd", reloaded.BuyerName);
    }

    // ── PO number IS now editable: column + canonical_json both updated ─────────────

    [Fact]
    public async Task ResolveAsync_EditsPoNumber_ToColumnAndCanonicalJson()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields(PoNumber: "PO-9999"));

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);

        // Column updated (the read path's PoNumber source).
        Assert.Equal("PO-9999", reloaded.PoNumber);

        // canonical_json mirror stays consistent (transform/Scriban read this).
        Assert.True(reloaded.CanonicalJson!.RootElement.TryGetProperty("poNumber", out var po));
        Assert.Equal("PO-9999", po.GetString());

        // Other header fields untouched.
        Assert.Equal("Old Buyer Ltd", reloaded.BuyerName);
        Assert.Equal("EUR", reloaded.Currency);
    }

    // ── Supplier DISPLAY name editable; routing (SupplierId) untouched ─────────────

    [Fact]
    public async Task ResolveAsync_EditsSupplierName_ChangesDisplayNotRouting()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var before = await ReloadAsync(db, orderId);
        var routingSupplierId = before.SupplierId; // must NOT change

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields(SupplierName: "  Northwind Trading OU  ")); // trimmed

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);

        // Display/document supplier name column updated + trimmed.
        Assert.Equal("Northwind Trading OU", reloaded.SupplierName);

        // canonical_json mirror.
        Assert.True(reloaded.CanonicalJson!.RootElement.TryGetProperty("supplierName", out var sn));
        Assert.Equal("Northwind Trading OU", sn.GetString());

        // CRITICAL: routing supplier id is unchanged — the edit is display-only.
        Assert.Equal(routingSupplierId, reloaded.SupplierId);
    }

    // ── PO number + supplier name coexist with date/currency/buyer in one request ──

    [Fact]
    public async Task ResolveAsync_AllFiveHeaderFields_PersistTogether()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var header = new ResolveHeaderFields(
            OrderDate:    new DateOnly(2026, 5, 1),
            BuyerName:    "Buyer Five",
            Currency:     "usd",
            PoNumber:     "PO-FIVE",
            SupplierName: "Supplier Five");

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            header);

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);
        Assert.Equal(new DateOnly(2026, 5, 1), reloaded.OrderDate);
        Assert.Equal("Buyer Five", reloaded.BuyerName);
        Assert.Equal("USD", reloaded.Currency);
        Assert.Equal("PO-FIVE", reloaded.PoNumber);
        Assert.Equal("Supplier Five", reloaded.SupplierName);

        // All five mirrored into canonical_json.
        var root = reloaded.CanonicalJson!.RootElement;
        Assert.Equal("2026-05-01",    root.GetProperty("orderDate").GetString());
        Assert.Equal("Buyer Five",    root.GetProperty("buyerName").GetString());
        Assert.Equal("USD",           root.GetProperty("currency").GetString());
        Assert.Equal("PO-FIVE",       root.GetProperty("poNumber").GetString());
        Assert.Equal("Supplier Five", root.GetProperty("supplierName").GetString());
    }

    // ── Whitespace-only PO number / supplier name are no-change ─────────────────────

    [Fact]
    public async Task ResolveAsync_WhitespacePoNumberAndSupplierName_AreNoChange()
    {
        var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = Build(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            new[] { new LineResolution(1, "SUP-1") },
            saveMappings: false,
            CancellationToken.None,
            new ResolveHeaderFields(PoNumber: "   ", SupplierName: "  "));

        Assert.True(result.IsSuccess);

        var reloaded = await ReloadAsync(db, orderId);
        // PO number column unchanged from the seed; supplier name still null (never set).
        Assert.Equal("PO-1001", reloaded.PoNumber);
        Assert.Null(reloaded.SupplierName);
    }
}
