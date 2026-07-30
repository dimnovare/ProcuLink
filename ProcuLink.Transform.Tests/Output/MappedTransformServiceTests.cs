using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for <see cref="MappedTransformService"/> (heart-piece-flex Phase 2): the override-aware
/// CSV/JSON builder. Covers value resolution from canonical + custom fields, manipulator application
/// (identical semantics to PoMappingEngine), and that the same NeedsReview / null-SupplierItemCode
/// validation guard the fixed transforms enforce still fires.
/// </summary>
public class MappedTransformServiceTests
{
    // ── The envelope comes from the one table, not from a literal here ────────
    //
    // WP-20 rerouted every transform through DeliveryMediaTypes and listed the sites it changed —
    // and missed this one, which is a LIVE path (OrderTransformService drives per-order,
    // per-revision and per-supplier mapping overrides through it). The values happened to agree
    // with the table, so nothing was visibly wrong and nothing bound them: changing the table's
    // Csv or Json row left this service, and the tests that pinned its literals, untouched.

    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Json)]
    public void TheOverrideBuildersEnvelope_ComesFromTheDeliveryMediaTypeTable(OutputFormat format)
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
            },
        };

        var result   = new MappedTransformService().Build(order, ov, format);
        var expected = ProcuLink.Core.Services.Delivery.DeliveryMediaTypes.For(format);

        result.ContentType.Should().Be(expected.ContentType,
            "the content type a document is stored and delivered with must come from the single table, not a literal in the builder");
        result.FileExtension.Should().Be(expected.FileExtension,
            "the extension a document is stored and delivered with must come from the single table, not a literal in the builder");
    }

    private static PurchaseOrderEntity BuildOrder(IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-9001",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 1),
            Currency   = "EUR",
            Status     = "ready",
        };

        order.Lines = (lines ?? new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = "SUP-2",
                Description = "Gadget", Quantity = 2m, Unit = "EA", UnitPrice = 5.5m,
                NeedsReview = false, Confidence = 1.0f,
            },
        }).ToList();

        return order;
    }

    private static string ReadCsv(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonDocument ReadJson(TransformResult result)
    {
        result.Content.Position = 0;
        return JsonDocument.Parse(result.Content);
    }

    [Fact]
    public void SupportsOverride_TrueForCsvAndJsonOnly()
    {
        MappedTransformService.SupportsOverride(OutputFormat.Csv).Should().BeTrue();
        MappedTransformService.SupportsOverride(OutputFormat.Json).Should().BeTrue();
        MappedTransformService.SupportsOverride(OutputFormat.Xml).Should().BeFalse();
        MappedTransformService.SupportsOverride(OutputFormat.Ubl).Should().BeFalse();
        MappedTransformService.SupportsOverride(OutputFormat.X12).Should().BeFalse();
        MappedTransformService.SupportsOverride(OutputFormat.CXml).Should().BeFalse();
    }

    [Fact]
    public void Build_Csv_EmitsOutputPathsAndResolvedValues()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
                Lines  =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                    ["qty"]  = new OutputFieldRule { OutputPath = "Qty",      CanonicalField = "Quantity" },
                },
            },
        };

        var result = new MappedTransformService().Build(order, ov, OutputFormat.Csv);
        var csv = ReadCsv(result);
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        result.ContentType.Should().Be("text/csv");
        result.FileExtension.Should().Be(".csv");
        rows[0].Should().Be("OrderRef,ItemCode,Qty");          // header column = output paths
        rows[1].Should().Be("PO-9001,SUP-1,3");                 // header value repeats + line 1
        rows[2].Should().Be("PO-9001,SUP-2,2");                 // line 2
    }

    [Fact]
    public void Build_Csv_AppliesManipulators_LikePoMappingEngine()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    // Replace "SUP-" → "S/", then the qty multiplied by 100 (cents).
                    ["code"] = new OutputFieldRule
                    {
                        OutputPath = "Code", CanonicalField = "SupplierItemCode",
                        FieldManipulators = { new ManipulatorEntry { Type = "Replace", Params = { "SUP-", "S/" } } },
                    },
                    ["cents"] = new OutputFieldRule
                    {
                        OutputPath = "PriceCents", CanonicalField = "UnitPrice",
                        FieldManipulators = { new ManipulatorEntry { Type = "Multiply", Params = { "100" } } },
                    },
                },
            },
        };

        var csv = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Code,PriceCents");
        rows[1].Should().Be("S/1,1000");   // SUP-1 → S/1 ; 10 * 100 = 1000
        rows[2].Should().Be("S/2,550");    // SUP-2 → S/2 ; 5.5 * 100 = 550
    }

    [Fact]
    public void Build_Csv_ResolvesCustomFields_HeaderAndLineScoped()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            CustomFields =
            {
                new CustomField { Key = "buyerGln", Label = "Buyer GLN", Scope = "header", Value = "4012345" },
                new CustomField
                {
                    Key = "lineNote", Label = "Note", Scope = "line",
                    LineValues = new Dictionary<int, string> { [1] = "first", [2] = "second" },
                },
            },
            Output = new OutputMappingConfig
            {
                Header = { ["gln"] = new OutputFieldRule { OutputPath = "GLN", CanonicalField = "buyerGln" } },
                Lines  = { ["note"] = new OutputFieldRule { OutputPath = "Note", CanonicalField = "lineNote" } },
            },
        };

        var csv = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("GLN,Note");
        rows[1].Should().Be("4012345,first");
        rows[2].Should().Be("4012345,second");
    }

    [Fact]
    public void Build_Csv_FixedValue_IsEmitted()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["src"] = new OutputFieldRule { OutputPath = "Source", FixedValue = "ProcuLink" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Source,Code");
        rows[1].Should().Be("ProcuLink,SUP-1");
    }

    [Fact]
    public void Build_Json_EmitsHeaderAndLineObjects()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "orderRef", CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "itemCode", CanonicalField = "SupplierItemCode" } },
            },
        };

        using var doc = ReadJson(new MappedTransformService().Build(order, ov, OutputFormat.Json));
        var root = doc.RootElement;

        root.GetProperty("header").GetProperty("orderRef").GetString().Should().Be("PO-9001");
        var lines = root.GetProperty("lines");
        lines.GetArrayLength().Should().Be(2);
        lines[0].GetProperty("itemCode").GetString().Should().Be("SUP-1");
        lines[1].GetProperty("itemCode").GetString().Should().Be("SUP-2");
    }

    [Fact]
    public void Build_ThrowsTransformValidationException_WhenALineNeedsReview()
    {
        var order = BuildOrder(new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Quantity = 1, UnitPrice = 1, NeedsReview = false,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = "SUP-2",
                Quantity = 1, UnitPrice = 1, NeedsReview = true, // unresolved
            },
        });

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var act = () => new MappedTransformService().Build(order, ov, OutputFormat.Csv);
        act.Should().Throw<TransformValidationException>();
    }

    [Fact]
    public void Build_ThrowsTransformValidationException_WhenSupplierCodeIsNull()
    {
        var order = BuildOrder(new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = null, // never resolved
                Quantity = 1, UnitPrice = 1, NeedsReview = false,
            },
        });

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var act = () => new MappedTransformService().Build(order, ov, OutputFormat.Csv);
        act.Should().Throw<TransformValidationException>();
    }

    [Fact]
    public void Build_ThrowsArgumentException_ForUnsupportedFormat()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "ref", CanonicalField = "PoNumber" } },
            },
        };

        var act = () => new MappedTransformService().Build(order, ov, OutputFormat.Xml);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_HoldsForReview_WhenLineTotalOverflowsDecimal_NotSilentZero()
    {
        // A pathological quantity × unit-price overflows decimal. The corrupt total must NOT be
        // silently delivered as 0 — the order is held for review (TransformValidationException),
        // the same mechanism an unresolved line uses.
        var order = BuildOrder(new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Big", Quantity = decimal.MaxValue, Unit = "EA", UnitPrice = decimal.MaxValue,
                NeedsReview = false, Confidence = 1.0f,
            },
        });

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var act = () => new MappedTransformService().Build(order, ov, OutputFormat.Csv);

        act.Should().Throw<TransformValidationException>()
            .Which.UnresolvedLineNumbers.Should().Contain(1);
    }

    // ── Phase 2: LoadCatalogProduct wired through the native CSV/JSON path ────────
    //
    // These prove the WIRING (offer⇔works): a CSV/JSON output rule using the user-selectable
    // LoadCatalogProduct manipulator emits the REAL catalog value on the native path. The catalog
    // row is supplied as a seeded lookup dict (the exact shape OrderServiceShared.BuildCatalogLookupAsync
    // produces) — NOT by hand-injecting the reserved __catalog_* keys — so the test fails if Build →
    // BuildLineRow → InjectCatalogRow → ManipulatorRegistry("LoadCatalogProduct") is not threaded.

    private static IReadOnlyDictionary<string, SupplierProduct> CatalogByCode(
        string code, decimal? price = null, string? unit = null, string? barcode = null) =>
        new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase)
        {
            [code] = new SupplierProduct
            {
                Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
                Code = code, Price = price, Unit = unit, Barcode = barcode, IsActive = true,
            },
        };

    [Fact]
    public void Build_Csv_LoadCatalogProduct_EmitsRealCatalogPrice_ForMatchedLine()
    {
        // Line 1 (SUP-1) is in the catalog @ 12.50; line 2 (SUP-2) is NOT in the catalog.
        var order   = BuildOrder();
        var catalog = CatalogByCode("SUP-1", price: 12.50m, unit: "BOX", barcode: "4006381333931");

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"]  = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                    // No source field needed — the manipulator reads the pre-injected catalog row.
                    ["price"] = new OutputFieldRule
                    {
                        OutputPath = "CatalogPrice",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "price" } } },
                    },
                },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, catalogLookup: catalog));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Code,CatalogPrice");
        rows[1].Should().Be("SUP-1,12.50");   // matched → REAL catalog price (invariant), not ""
        rows[2].Should().Be("SUP-2,");          // unmatched → "" exactly as before the wiring
    }

    [Fact]
    public void Build_Csv_LoadCatalogProduct_ReturnsEmpty_WhenNoCatalogLookupSupplied()
    {
        // No catalogLookup → no reserved keys injected → the manipulator returns "" (byte-identical
        // to the pre-wiring behaviour). This is the regression guard the wiring must not break.
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["price"] = new OutputFieldRule
                    {
                        OutputPath = "CatalogPrice",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "price" } } },
                    },
                },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv)); // no catalogLookup
        var rows = csv.Replace("\r\n", "\n").Split('\n'); // keep trailing-empty rows (do NOT trim)

        rows[0].Should().Be("CatalogPrice"); // header
        rows[1].Should().Be("");             // line 1 → manipulator returns "" with no catalog
        rows[2].Should().Be("");             // line 2 → ""
    }

    [Fact]
    public void Build_Json_LoadCatalogProduct_EmitsRealCatalogCodeAndUnit_ForMatchedLine()
    {
        var order   = BuildOrder();
        var catalog = CatalogByCode("SUP-1", price: 99m, unit: "PCS", barcode: "5012345678900");

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["catCode"] = new OutputFieldRule
                    {
                        OutputPath = "catalogCode",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "code" } } },
                    },
                    ["catUnit"] = new OutputFieldRule
                    {
                        OutputPath = "catalogUnit",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "unit" } } },
                    },
                    ["catBarcode"] = new OutputFieldRule
                    {
                        OutputPath = "catalogBarcode",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "barcode" } } },
                    },
                },
            },
        };

        using var doc = ReadJson(new MappedTransformService().Build(order, ov, OutputFormat.Json, catalogLookup: catalog));
        var lines = doc.RootElement.GetProperty("lines");

        lines[0].GetProperty("catalogCode").GetString().Should().Be("SUP-1");
        lines[0].GetProperty("catalogUnit").GetString().Should().Be("PCS");
        lines[0].GetProperty("catalogBarcode").GetString().Should().Be("5012345678900");
        // Line 2 has no catalog row → all catalog fields render "".
        lines[1].GetProperty("catalogCode").GetString().Should().Be("");
        lines[1].GetProperty("catalogUnit").GetString().Should().Be("");
    }

    [Fact]
    public void Build_Csv_LoadCatalogProduct_MatchesByManufacturerPartNumber_WhenCodeMisses()
    {
        // The line's SupplierItemCode is not in the catalog, but its ManufacturerPartNumber is —
        // mirrors ScribanOrderModel.BuildLine's fallback resolution order.
        var order = BuildOrder(new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                ManufacturerPartNumber = "MPN-42", Quantity = 1, UnitPrice = 1, NeedsReview = false,
            },
        });
        var catalog = CatalogByCode("MPN-42", price: 7.77m);

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["price"] = new OutputFieldRule
                    {
                        OutputPath = "CatalogPrice",
                        FieldManipulators = { new ManipulatorEntry { Type = "LoadCatalogProduct", Params = { "price" } } },
                    },
                },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, catalogLookup: catalog));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().Be("7.77");
    }

    // ── F-1 Seam A: bind an ARBITRARY source field via CanonicalField == "src::{tokenId}" ────────
    //
    // These prove the row bag is widened with the reserved src:: namespace and that every binding
    // reader (CanonicalField here) resolves a token the canonical model does NOT model.

    [Fact]
    public void Build_Csv_BindsHeaderSourceToken_ViaSrcCanonicalField_Verbatim()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>
        {
            // A header field the canonical model has no slot for — e.g. a buyer VAT id.
            new("cell:r1c9", "Buyer VAT · header row", "EE100200300", "header"),
        };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["vat"] = new OutputFieldRule { OutputPath = "BuyerVat", CanonicalField = "src::cell:r1c9" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("BuyerVat,Code");
        rows[1].Should().Be("EE100200300,SUP-1");   // the arbitrary source field emitted verbatim
        rows[2].Should().Be("EE100200300,SUP-2");
    }

    [Fact]
    public void Build_Csv_BindsLineSourceToken_ToTheCorrectLine_ViaSrcCanonicalField()
    {
        var order  = BuildOrder();
        // Line 1 lives at source data-row 2 (cell:r2c4), line 2 at data-row 3 (cell:r3c4).
        var tokens = new List<SourceToken>
        {
            new("cell:r2c4", "EAN · row 2", "EAN-FIRST",  "line"),
            new("cell:r3c4", "EAN · row 3", "EAN-SECOND", "line"),
        };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                    ["ean"]  = new OutputFieldRule { OutputPath = "Ean",  CanonicalField = "src::cell:r2c4" },
                },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Code,Ean");
        // Line 1 binds cell:r2c4 → its OWN value; line 2's bag does NOT carry cell:r2c4 → empty (never
        // reads line 1's value). This is the "line[2] never reads line[1]" guarantee.
        rows[1].Should().Be("SUP-1,EAN-FIRST");
        rows[2].Should().Be("SUP-2,");
    }

    // ── F-1 Phase 4: ONE rule bound to the RELATIVE key emits EACH line's OWN value ─────────────────

    [Fact]
    public void Build_Csv_BindsRelativeLineKey_EmitsEachLinesOwnValue()
    {
        var order  = BuildOrder();
        // Line 1 = data-row 2 (cell:r2c4), line 2 = data-row 3 (cell:r3c4). Relative key = cell:c4.
        var tokens = new List<SourceToken>
        {
            new("cell:r2c4", "EAN · row 2", "EAN-FIRST",  "line"),
            new("cell:r3c4", "EAN · row 3", "EAN-SECOND", "line"),
        };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                    // ONE rule bound to the RELATIVE column key — emits EACH line's own cell.
                    ["ean"]  = new OutputFieldRule { OutputPath = "Ean",  CanonicalField = "src::cell:c4" },
                },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Code,Ean");
        rows[1].Should().Be("SUP-1,EAN-FIRST");    // line 1 → its OWN row-2 value
        rows[2].Should().Be("SUP-2,EAN-SECOND");   // line 2 → its OWN row-3 value (NOT empty, NOT line 1's)
    }

    [Fact]
    public void BuildLineRow_InjectsRelativeAlias_HoldingEachLinesOwnValue_EdiAndAllFormats()
    {
        // Proves the alias injection at the row-bag level for EVERY per-line format (incl. EDI, which is
        // not an emitter target). Each line's bag must carry the RELATIVE key holding THAT line's value.
        var order  = BuildOrder();
        var line1  = order.Lines.Single(l => l.LineNumber == 1);
        var line2  = order.Lines.Single(l => l.LineNumber == 2);
        var ov     = new OrderMappingOverride();

        var tokens = new List<SourceToken>
        {
            // EDIFACT: seg:LIN[n].el5 → relative seg:LIN.el5
            new("seg:LIN[1].el5", "LIN warehouse", "WH-A", "line"),
            new("seg:LIN[2].el5", "LIN warehouse", "WH-B", "line"),
            // XML: …/Line[n]/Batch → relative …/Line/Batch
            new("/Order/Lines/Line[1]/Batch", "Batch", "B-A", "line"),
            new("/Order/Lines/Line[2]/Batch", "Batch", "B-B", "line"),
            // JSON: json:/lines/{i}/sku → relative json:/lines/*/sku
            new("json:/lines/0/sku", "sku", "J-A", "line"),
            new("json:/lines/1/sku", "sku", "J-B", "line"),
        };

        var row1 = MappedTransformService.BuildLineRow(order, ov, line1, catalogLookup: null, sourceTokens: tokens);
        var row2 = MappedTransformService.BuildLineRow(order, ov, line2, catalogLookup: null, sourceTokens: tokens);

        // Line 1's bag: the relative keys hold line 1's OWN values.
        row1["src::seg:LIN.el5"].Should().Be("WH-A");
        row1["src::/Order/Lines/Line/Batch"].Should().Be("B-A");
        row1["src::json:/lines/*/sku"].Should().Be("J-A");

        // Line 2's bag: the SAME relative keys hold line 2's OWN values (never line 1's).
        row2["src::seg:LIN.el5"].Should().Be("WH-B");
        row2["src::/Order/Lines/Line/Batch"].Should().Be("B-B");
        row2["src::json:/lines/*/sku"].Should().Be("J-B");

        // The absolute id still lands ONLY in its own line's bag (no cross-line leak).
        row1.Should().ContainKey("src::seg:LIN[1].el5");
        row1.Should().NotContainKey("src::seg:LIN[2].el5");
        row2.Should().ContainKey("src::seg:LIN[2].el5");
        row2.Should().NotContainKey("src::seg:LIN[1].el5");
    }

    [Fact]
    public void Build_Json_BindsSourceToken_PreservesEuLocaleValueVerbatim()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>
        {
            // EU locale price string must survive byte-for-byte — never numeric-parsed in the bag.
            new("/Order/Header/Total", "Total", "1.234,56", "header"),
        };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["t"] = new OutputFieldRule { OutputPath = "total", CanonicalField = "src::/Order/Header/Total" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "code", CanonicalField = "SupplierItemCode" } },
            },
        };

        using var doc = ReadJson(new MappedTransformService().Build(order, ov, OutputFormat.Json, tokens));
        doc.RootElement.GetProperty("header").GetProperty("total").GetString().Should().Be("1.234,56");
    }

    // ── F-1 Seam B: the typed OutputFieldRule.SourceToken property (bare id; resolver prefixes src::) ─

    [Fact]
    public void Build_Csv_BindsTypedSourceToken_EmitsTokenValue()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>
        {
            new("seg:RFF[1].el2", "RFF reference number", "REF-XYZ", "header"),
        };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                // BARE token id on the typed property (no src:: prefix — the resolver adds it).
                Header = { ["ref"] = new OutputFieldRule { OutputPath = "Ref", SourceToken = "seg:RFF[1].el2" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[0].Should().Be("Ref,Code");
        rows[1].Should().Be("REF-XYZ,SUP-1");
    }

    [Fact]
    public void ResolveRule_SourceTokenPrecedence_WinsOverFixedValue()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken> { new("cell:r1c2", "X", "FROM-TOKEN", "header") };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header =
                {
                    // Both a token AND a fixed value set — the token must win (Expr → SourceToken → Fixed → Canon).
                    ["x"] = new OutputFieldRule { OutputPath = "X", SourceToken = "cell:r1c2", FixedValue = "FROM-FIXED" },
                },
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().StartWith("FROM-TOKEN,");
    }

    [Fact]
    public void ResolveRule_NotFoundSourceToken_FallsThroughToFixedValue_NeverEmitsLiteralSrc()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>(); // no tokens → the bound id is absent from the bag

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header =
                {
                    ["x"] = new OutputFieldRule { OutputPath = "X", SourceToken = "cell:r9c9", FixedValue = "FALLBACK" },
                },
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().StartWith("FALLBACK,");        // fell through to FixedValue
        csv.Should().NotContain("src::");               // never emits a literal "src::…"
    }

    [Fact]
    public void ResolveRule_NotFoundSourceToken_NoFixed_FallsThroughToCanonicalField()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>();

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header =
                {
                    // Token absent, no FixedValue → must fall through to the canonical field.
                    ["po"] = new OutputFieldRule { OutputPath = "Po", SourceToken = "cell:r9c9", CanonicalField = "PoNumber" },
                },
                Lines = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv  = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().StartWith("PO-9001,");
    }

    [Fact]
    public void Build_Csv_EscapesValuesContainingCommas()
    {
        var order = BuildOrder(new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget, large", Quantity = 1, UnitPrice = 1, NeedsReview = false,
            },
        });

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines = { ["desc"] = new OutputFieldRule { OutputPath = "Description", CanonicalField = "Description" } },
            },
        };

        var csv = ReadCsv(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        var rows = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        rows[1].Should().Be("\"Widget, large\"");
    }
}
