using System.Text;
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
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using ProcuLink.Transform.Tokenizing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// F-1 Phase 4c — a per-order <see cref="OutputFieldRule.SourceToken"/> binding (bind ANY source
/// field, including a RELATIVE repeating-column key like <c>cell:c4</c>) must AUTO-APPLY to FUTURE
/// orders from the SAME supplier once promoted with "Save mappings for this supplier".
///
/// The whole point of the F-1 binding is that it is a property INSIDE the
/// <see cref="OrderMappingOverride.Output"/> rules, so:
/// <list type="bullet">
///   <item><b>Promotion</b> — <see cref="PromoteMappingService"/> copies <c>Output</c> VERBATIM onto
///        <see cref="PoMappingConfig.Output"/>, so the <c>SourceToken</c> rules ride along (no
///        field-by-field rebuild that would drop the binding). A source-binding-only Output (rules
///        present, none carrying a canonical field) must still count as USABLE — never a silent
///        "nothing to promote".</item>
///   <item><b>Apply</b> — the supplier-promoted apply path threads the FUTURE order's OWN source
///        tokens (rebuilt from that order's <see cref="SourceCapture.TokensJson"/>) into
///        <see cref="MappedTransformService.Build"/>, so the promoted relative binding resolves to
///        each future line's OWN cell value — not the original order's.</item>
/// </list>
///
/// This test exercises the REAL machinery end-to-end (no new concept):
/// <c>OrderMappingOverrideService → PromoteMappingService → PoMappingService → OrderService.TransformAsync</c>,
/// plus a direct <see cref="MappedTransformService.Build"/> assertion against the second order's tokens.
/// </summary>
public class PromotedSourceBindingAppliesToFutureOrdersTests
{
    // Column 4 (1-based) carries a repeating non-canonical value (an EAN) the canonical model has no
    // slot for. Source data-row 2 = line 1, data-row 3 = line 2 (row 1 is the header).
    private const string AbsoluteLineToken = "cell:r2c4"; // binds line 1 specifically
    private const string RelativeLineToken = "cell:c4";   // binds the repeating column for EVERY line

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── service wiring ──────────────────────────────────────────────────────────

    /// <summary>OrderService over the same db with the REAL PoMappingService (so the supplier-promoted
    /// read path is exercised) and a byte-capturing storage mock.</summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) BuildOrderService(ProcuLinkDbContext db)
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
            new PoMappingService(db), // REAL — the supplier-promoted read path under test
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    private static PromoteMappingService BuildPromoteService(ProcuLinkDbContext db) =>
        new(db, new PoMappingService(db));

    // ── seeding ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a two-line resolved order with the column-4 source tokens supplied per line.
    /// Returns the order id. Lines carry resolved SupplierItemCodes so the transform never holds them.
    /// </summary>
    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string poNumber, string line1Col4, string line2Col4)
    {
        var orderId = Guid.NewGuid();

        // The token bag mirrors the Phase-1 writer shape ({id,label,value,group}). The same
        // production deserializer (SourceTokenSerialization.FromTokensJson) reads it at apply time.
        var tokensJson = JsonSerializer.Serialize(new[]
        {
            new { id = "cell:r2c4", label = "EAN · row 2", value = line1Col4, group = "line" },
            new { id = "cell:r3c4", label = "EAN · row 3", value = line2Col4, group = "line" },
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = poNumber,
            BuyerName  = "F1-4c Buyer",
            OrderDate  = new DateOnly(2026, 6, 18),
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
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
            SourceCapture = new SourceCapture
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, Format = "csv",
                CapturedAt = DateTime.UtcNow,
                TokensJson = JsonDocument.Parse(tokensJson),
            },
        });

        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// The per-order output mapping the FIRST order carries: a RELATIVE column binding (cell:c4 → each
    /// line's own value), an ABSOLUTE binding (cell:r2c4 → line 1 specifically), AND a normal canonical
    /// line rule. NONE of these is a header rule — proving a source-binding-only Output promotes.
    /// </summary>
    private static OutputMappingConfig SourceBoundOutput() => new()
    {
        Lines =
        {
            ["code"]    = new OutputFieldRule { OutputPath = "Code",       CanonicalField = "SupplierItemCode" },
            ["eanRel"]  = new OutputFieldRule { OutputPath = "EanRel",     SourceToken    = RelativeLineToken },
            ["eanAbs"]  = new OutputFieldRule { OutputPath = "EanAbs",     SourceToken    = AbsoluteLineToken },
        },
    };

    // ── (1) PROMOTION carries the SourceToken rules verbatim & counts a binding-only Output ──────

    [Fact]
    public async Task Promote_SourceBindingOnlyOutput_CopiesSourceTokenRulesVerbatim_AndCountsAsUsable()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "F1-4c Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var firstOrderId = await SeedOrderAsync(db, orgId, supplierId, "PO-FIRST", "VAL-A", "VAL-B");

        // Store the per-order override (writes to the order's canonical_json).
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, firstOrderId,
            new OrderMappingOverride { Output = SourceBoundOutput() }, CancellationToken.None);

        // Promote → supplier mapping.
        var result = await BuildPromoteService(db).PromoteAsync(orgId, firstOrderId, CancellationToken.None);

        Assert.NotNull(result);
        // A source-binding-only Output is USABLE — never a silent "nothing to promote".
        Assert.False(result!.NothingToPromote);
        Assert.Equal(3, result.OutputLineFieldsPromoted); // code + eanRel + eanAbs all promoted
        Assert.Equal(0, result.OutputHeaderFieldsPromoted);

        // The promoted PoMappingConfig.Output carries the SourceToken rules VERBATIM (the binding rode
        // along — it was NOT rebuilt field-by-field and dropped).
        var promoted = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(promoted?.Output);
        Assert.Equal(RelativeLineToken, promoted!.Output!.Lines["eanRel"].SourceToken);
        Assert.Equal(AbsoluteLineToken, promoted.Output.Lines["eanAbs"].SourceToken);
        Assert.Equal("SupplierItemCode", promoted.Output.Lines["code"].CanonicalField);
    }

    // ── (2) APPLY to a FUTURE order — emits the SECOND order's OWN per-line source values ────────

    /// <summary>
    /// The deliverable: promote the FIRST order's source binding, then transform a SECOND same-supplier
    /// order that has NO per-order override. The promoted relative binding (cell:c4) must resolve to the
    /// SECOND order's OWN per-line column value (VAL-A2 on line 1, VAL-B2 on line 2) — proving the binding
    /// applies to future orders, threaded with THAT order's tokens. The absolute binding (cell:r2c4)
    /// resolves only line 1 (line 2's bag never carries it). The canonical rule still resolves.
    /// </summary>
    [Fact]
    public async Task Transform_SecondOrder_PromotedSourceBinding_EmitsSecondOrdersOwnPerLineValues()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "F1-4c Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // FIRST order with the per-order source-bound override; promote it to the supplier.
        var firstOrderId = await SeedOrderAsync(db, orgId, supplierId, "PO-FIRST", "VAL-A", "VAL-B");
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, firstOrderId,
            new OrderMappingOverride { Output = SourceBoundOutput() }, CancellationToken.None);
        var promote = await BuildPromoteService(db).PromoteAsync(orgId, firstOrderId, CancellationToken.None);
        Assert.False(promote!.NothingToPromote);

        // SECOND, same-schema order from the SAME supplier — NO per-order override of its own. Its OWN
        // source tokens carry DIFFERENT column-4 values, so we can prove the binding reads the future
        // order's own cells (not the first order's VAL-A / VAL-B).
        var secondOrderId = await SeedOrderAsync(db, orgId, supplierId, "PO-SECOND", "VAL-A2", "VAL-B2");

        var (svc, captured) = BuildOrderService(db);
        var result = await svc.TransformAsync(orgId, secondOrderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        // Header = the promoted output paths (proves the supplier-promoted mapping drove the output).
        Assert.Equal("Code,EanRel,EanAbs", rows[0]);

        // Line 1: relative cell:c4 → VAL-A2 (this order's own), absolute cell:r2c4 → VAL-A2 (line 1).
        Assert.Equal("SUP-1,VAL-A2,VAL-A2", rows[1]);
        // Line 2: relative cell:c4 → VAL-B2 (this order's OWN second cell). The absolute cell:r2c4 is
        // NOT in line 2's bag (it addresses line 1 only) → empty. This is the "line 2 never reads
        // line 1's value" guarantee, now proven for a PROMOTED binding applied to a future order.
        Assert.Equal("SUP-2,VAL-B2,", rows[2]);

        // And NONE of the first order's values leaked through the promoted binding.
        Assert.DoesNotContain("VAL-A,", csv);
        Assert.DoesNotContain("VAL-B,", csv);
    }

    // ── (3) Preview-shaped resolution parity — the SAME MappedTransformService.Build apply path ──

    /// <summary>
    /// The preview path (OrdersController) and the transform path both call
    /// <see cref="MappedTransformService.Build"/> with the order's OWN tokens. This asserts that calling
    /// Build directly with the promoted supplier Output + the second order's tokens emits the second
    /// order's own per-line values — the same resolution the preview surfaces, so preview == delivered.
    /// </summary>
    [Fact]
    public async Task Build_WithPromotedSupplierOutput_AndSecondOrdersTokens_ResolvesSecondOrdersValues()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "F1-4c Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // Promote the FIRST order's source binding to the supplier.
        var firstOrderId = await SeedOrderAsync(db, orgId, supplierId, "PO-FIRST", "VAL-A", "VAL-B");
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, firstOrderId,
            new OrderMappingOverride { Output = SourceBoundOutput() }, CancellationToken.None);
        await BuildPromoteService(db).PromoteAsync(orgId, firstOrderId, CancellationToken.None);

        // Read the PROMOTED supplier Output back and wrap it as a synthetic override (exactly what both
        // the preview controller and the transform service do for an order with no per-order override).
        var supplierConfig = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        var syntheticOverride = new OrderMappingOverride { Output = supplierConfig!.Output };

        // SECOND order + its OWN tokens, rebuilt via the production deserializer.
        var secondOrderId = await SeedOrderAsync(db, orgId, supplierId, "PO-SECOND", "VAL-A2", "VAL-B2");
        var secondOrder = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Supplier).Include(o => o.SourceCapture)
            .FirstAsync(o => o.Id == secondOrderId && o.OrgId == orgId);
        IReadOnlyList<SourceToken> secondTokens =
            SourceTokenSerialization.FromTokensJson(secondOrder.SourceCapture!.TokensJson);

        var transform = new MappedTransformService()
            .Build(secondOrder, syntheticOverride, OutputFormat.Csv, sourceTokens: secondTokens);
        transform.Content.Position = 0;
        using var reader = new StreamReader(transform.Content);
        var csv  = await reader.ReadToEndAsync();
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        Assert.Equal("Code,EanRel,EanAbs", rows[0]);
        Assert.Equal("SUP-1,VAL-A2,VAL-A2", rows[1]); // line 1: relative + absolute both → this order's line-1 cell
        Assert.Equal("SUP-2,VAL-B2,", rows[2]);        // line 2: relative → this order's line-2 cell; absolute empty
    }
}
