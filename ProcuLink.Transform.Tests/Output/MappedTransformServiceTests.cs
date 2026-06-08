using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for <see cref="MappedTransformService"/> (heart-piece-flex Phase 2): the override-aware
/// CSV/JSON builder. Covers value resolution from canonical + custom fields, manipulator application
/// (identical semantics to PoMappingEngine), and that the same NeedsReview / null-SupplierItemCode
/// validation guard the fixed transforms enforce still fires.
/// </summary>
public class MappedTransformServiceTests
{
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
