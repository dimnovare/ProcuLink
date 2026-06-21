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
/// Tests for the per-field Scriban <c>Expression</c> support (heart-piece-flex flexible mapping):
///   • the sandboxed <see cref="ScribanFieldEvaluator"/> itself (scope, arithmetic, interpolation,
///     invalid-expression safety, unknown-member relaxation);
///   • the <see cref="OutputFieldRule.Expression"/> wiring in <see cref="MappedTransformService"/>
///     (precedence over canonicalField/fixedValue, chain-after-expression, header vs line scope);
///   • the <see cref="SourceFieldRule.Expression"/> wiring in <see cref="SourceMapReDerive"/>;
///   • the additive invariant: with NO Expression set, output is byte-for-byte identical to before.
/// </summary>
public class ScribanExpressionMappingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static string[] Rows(TransformResult result) =>
        ReadCsv(result).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static JsonDocument ReadJson(TransformResult result)
    {
        result.Content.Position = 0;
        return JsonDocument.Parse(result.Content);
    }

    // ── ScribanFieldEvaluator: direct unit tests ───────────────────────────────

    [Fact]
    public void Evaluator_Header_ResolvesOrderMember()
    {
        var row = new Dictionary<string, string> { ["PoNumber"] = "PO-42", ["Currency"] = "EUR" };

        var result = ScribanFieldEvaluator.EvaluateHeader("{{ order.PoNumber }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("PO-42");
    }

    [Fact]
    public void Evaluator_Line_ArithmeticOnNumericFields()
    {
        // Quantity * UnitPrice must be DECIMAL math, not string concat.
        var row = new Dictionary<string, string>
        {
            ["Quantity"]  = "3",
            ["UnitPrice"] = "10",
        };

        var result = ScribanFieldEvaluator.EvaluateLine("{{ line.Quantity * line.UnitPrice }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("30");
    }

    [Fact]
    public void Evaluator_Line_StringInterpolationAcrossOrderAndLine()
    {
        var row = new Dictionary<string, string>
        {
            ["Currency"]         = "EUR",
            ["SupplierItemCode"] = "SUP-7",
        };

        var result = ScribanFieldEvaluator.EvaluateLine(
            "{{ order.Currency }}-{{ line.SupplierItemCode }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("EUR-SUP-7");
    }

    [Fact]
    public void Evaluator_InvalidExpression_ReturnsFailure_NeverThrows()
    {
        var row = new Dictionary<string, string> { ["PoNumber"] = "PO-1" };

        // A binary operator with a missing right operand — a genuine compile/parse error.
        var act = () => ScribanFieldEvaluator.EvaluateHeader("{{ order.PoNumber + * }}", row);

        var result = act.Should().NotThrow().Subject;
        result.Ok.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Evaluator_ArithmeticOnNonNumericString_NeverThrows()
    {
        var row = new Dictionary<string, string> { ["PoNumber"] = "PO-1" };

        // Scriban coerces a non-numeric string in an arithmetic context rather than throwing; the
        // contract we depend on is simply that the evaluator NEVER throws and always returns a result.
        var act = () => ScribanFieldEvaluator.EvaluateHeader("{{ order.PoNumber * 2 }}", row);

        var result = act.Should().NotThrow().Subject;
        // Either outcome is acceptable; what matters is no exception escapes into the transform.
        (result.Ok ? result.Value : result.Error).Should().NotBeNull();
    }

    [Fact]
    public void Evaluator_UnknownMember_RendersEmpty_DoesNotThrow()
    {
        var row = new Dictionary<string, string> { ["PoNumber"] = "PO-1" };

        var result = ScribanFieldEvaluator.EvaluateHeader("{{ order.DoesNotExist }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public void Evaluator_HeaderScope_HasNoLineObject()
    {
        // A header expression referencing `line` must not throw — relaxed access renders empty.
        var row = new Dictionary<string, string> { ["PoNumber"] = "PO-1" };

        var result = ScribanFieldEvaluator.EvaluateHeader("{{ line.Quantity }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public void Evaluator_LiteralText_AroundExpression_IsPreserved()
    {
        var row = new Dictionary<string, string> { ["PoNumber"] = "9001" };

        var result = ScribanFieldEvaluator.EvaluateHeader("ORD-{{ order.PoNumber }}", row);

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("ORD-9001");
    }

    // ── OutputFieldRule.Expression precedence + chaining ────────────────────────

    [Fact]
    public void Output_Expression_TakesPrecedenceOverCanonicalFieldAndFixedValue()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                // Expression set ALONGSIDE a (conflicting) canonicalField + fixedValue — expression wins.
                Header =
                {
                    ["po"] = new OutputFieldRule
                    {
                        OutputPath     = "Ref",
                        Expression     = "ORD-{{ order.PoNumber }}",
                        CanonicalField = "Currency",   // would be EUR if used
                        FixedValue     = "ignored",     // would win over field if used
                    },
                },
                Lines =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        rows[0].Should().Be("Ref,Code");
        rows[1].Should().Be("ORD-PO-9001,SUP-1");
    }

    [Fact]
    public void Output_LineExpression_Arithmetic_ComputesLineTotal()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["total"] = new OutputFieldRule
                    {
                        OutputPath = "Total",
                        Expression = "{{ line.Quantity * line.UnitPrice }}",
                    },
                },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        rows[0].Should().Be("Total");
        rows[1].Should().Be("30");    // 3 * 10 (decimal scale 0)
        rows[2].Should().Be("11.0");  // 2 * 5.5 (decimal preserves scale → 11.0)
    }

    [Fact]
    public void Output_Expression_ThenManipulatorChain_AppliesInOrder()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    // Expression builds "EUR-SUP-1", THEN Replace "EUR-" → "" leaves "SUP-1".
                    ["code"] = new OutputFieldRule
                    {
                        OutputPath        = "Code",
                        Expression        = "{{ order.Currency }}-{{ line.SupplierItemCode }}",
                        FieldManipulators = { new ManipulatorEntry { Type = "Replace", Params = { "EUR-", "" } } },
                    },
                },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");
        rows[2].Should().Be("SUP-2");
    }

    [Fact]
    public void Output_HeaderExpression_InJson()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["ref"] = new OutputFieldRule { OutputPath = "orderRef", Expression = "{{ order.PoNumber }}/{{ order.Currency }}" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "itemCode", CanonicalField = "SupplierItemCode" } },
            },
        };

        using var doc = ReadJson(new MappedTransformService().Build(order, ov, OutputFormat.Json));
        doc.RootElement.GetProperty("header").GetProperty("orderRef").GetString().Should().Be("PO-9001/EUR");
    }

    [Fact]
    public void Output_InvalidExpression_FallsBackToCanonicalField_DoesNotCrash()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    ["code"] = new OutputFieldRule
                    {
                        OutputPath     = "Code",
                        Expression     = "{{ line.SupplierItemCode + * }}",  // broken — won't compile
                        CanonicalField = "SupplierItemCode",                 // fallback
                    },
                },
            },
        };

        var act = () => new MappedTransformService().Build(order, ov, OutputFormat.Csv);
        var result = act.Should().NotThrow().Subject;

        var rows = Rows(result);
        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");   // fell back to the canonical field
    }

    [Fact]
    public void Output_CustomHeaderField_ReachableFromExpression()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            CustomFields =
            {
                new CustomField { Key = "buyerGln", Label = "GLN", Scope = "header", Value = "4012345" },
            },
            Output = new OutputMappingConfig
            {
                Header = { ["gln"] = new OutputFieldRule { OutputPath = "GLN", Expression = "GLN:{{ order.buyerGln }}" } },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        rows[0].Should().Be("GLN");
        rows[1].Should().Be("GLN:4012345");
    }

    // ── SourceFieldRule.Expression wiring (SourceMapReDerive) ────────────────────

    [Fact]
    public void SourceMap_HeaderExpression_OverridesParsedValue()
    {
        var headerRow = new Dictionary<string, string>
        {
            ["PoNumber"]     = "ORIGINAL",
            ["OrderDate"]    = "2026-01-01",
            ["BuyerName"]    = "Acme",
            ["Currency"]     = "EUR",
            ["SupplierName"] = "Supplier Co",
        };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { Expression = "{{ order.Currency }}-{{ order.BuyerName }}" },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(headerRow, ov, Array.Empty<SourceToken>());

        result["PoNumber"].Should().Be("EUR-Acme");
    }

    [Fact]
    public void SourceMap_LineExpression_Arithmetic()
    {
        var lineRow = new Dictionary<string, string>
        {
            ["SupplierItemCode"] = "SUP-1",
            ["Quantity"]         = "4",
            ["UnitPrice"]        = "2.5",
            ["LineTotal"]        = "10",
        };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["LineTotal"] = new SourceFieldRule { Expression = "{{ line.Quantity * line.UnitPrice }}" },
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(lineRow, ov, Array.Empty<SourceToken>());

        result["LineTotal"].Should().Be("10.0");  // 4 * 2.5 (decimal preserves scale → 10.0)
    }

    [Fact]
    public void SourceMap_Expression_TakesPrecedenceOverSourceTokenAndFixedValue()
    {
        var headerRow = new Dictionary<string, string> { ["PoNumber"] = "ORIGINAL", ["Currency"] = "EUR" };
        var tokens = new[] { new SourceToken(Id: "cell:r1c1", Label: "x", Value: "FROM-TOKEN", Group: "header") };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule
                {
                    Expression  = "EXPR-{{ order.Currency }}",
                    SourceToken = "cell:r1c1",   // would win if no expression
                    FixedValue  = "FIXED",        // would win if no token/expression
                },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(headerRow, ov, tokens);

        result["PoNumber"].Should().Be("EXPR-EUR");
    }

    [Fact]
    public void SourceMap_Expression_ThenManipulatorChain()
    {
        var headerRow = new Dictionary<string, string> { ["PoNumber"] = "x", ["Currency"] = "eur" };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule
                {
                    Expression  = "{{ order.Currency }}",
                    Manipulators = { new ManipulatorEntry { Type = "Replace", Params = { "eur", "EUR" } } },
                },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(headerRow, ov, Array.Empty<SourceToken>());

        result["PoNumber"].Should().Be("EUR");
    }

    [Fact]
    public void SourceMap_InvalidExpression_FallsBackToTokenThenFixedThenPassThrough()
    {
        var headerRow = new Dictionary<string, string> { ["PoNumber"] = "PASS-THROUGH" };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { Expression = "{{ order.PoNumber + * }}" }, // broken
            },
        };

        var act = () => SourceMapReDerive.ApplyToHeaderRow(headerRow, ov, Array.Empty<SourceToken>());
        var result = act.Should().NotThrow().Subject;

        // No token, no fixed value → falls all the way through to the original parsed value.
        result["PoNumber"].Should().Be("PASS-THROUGH");
    }

    // ── F-1: Scriban indexer access to a bound source field via order["src::…"] ─────────────────

    [Fact]
    public void Output_ScribanExpression_ReadsBoundSourceToken_ViaIndexer()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken> { new("cell:r1c9", "Buyer VAT", "EE777", "header") };

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                // The src:: key is a legal ScriptObject member accessed by the indexer form.
                Header = { ["vat"] = new OutputFieldRule { OutputPath = "Vat", Expression = "VAT:{{ order[\"src::cell:r1c9\"] }}" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        rows[0].Should().Be("Vat,Code");
        rows[1].Should().Be("VAT:EE777,SUP-1");
    }

    [Fact]
    public void Output_ScribanExpression_MissingSrcKey_RendersEmpty_FailOpen()
    {
        var order  = BuildOrder();
        var tokens = new List<SourceToken>(); // no tokens → src::missing is absent from the bag

        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["x"] = new OutputFieldRule { OutputPath = "X", Expression = "[{{ order[\"src::missing\"] }}]" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv, tokens));
        rows[0].Should().Be("X,Code");
        rows[1].Should().Be("[],SUP-1");   // relaxed member access → empty, never throws
    }

    // ── Additive invariant: no Expression → byte-for-byte unchanged ─────────────

    [Fact]
    public void NoExpression_Csv_IsByteForByteIdenticalToCanonicalFieldMapping()
    {
        var order = BuildOrder();

        // Identical rules, one WITHOUT Expression, one WITH a NULL Expression — must match.
        var withoutExpr = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber" } },
                Lines  =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                    ["qty"]  = new OutputFieldRule { OutputPath = "Qty",  CanonicalField = "Quantity" },
                },
            },
        };
        var withNullExpr = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber", Expression = null } },
                Lines  =
                {
                    ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode", Expression = null },
                    ["qty"]  = new OutputFieldRule { OutputPath = "Qty",  CanonicalField = "Quantity", Expression = null },
                },
            },
        };

        var a = ReadCsv(new MappedTransformService().Build(order, withoutExpr, OutputFormat.Csv));
        var b = ReadCsv(new MappedTransformService().Build(order, withNullExpr, OutputFormat.Csv));

        b.Should().Be(a);
        // And the concrete expected content (guards against both paths changing together).
        var rows = a.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        rows[0].Should().Be("Ref,Code,Qty");
        rows[1].Should().Be("PO-9001,SUP-1,3");
        rows[2].Should().Be("PO-9001,SUP-2,2");
    }

    [Fact]
    public void NoExpression_BlankExpression_IsIgnored_FallsBackToField()
    {
        var order = BuildOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines =
                {
                    // Whitespace-only expression must be treated as "no expression".
                    ["code"] = new OutputFieldRule { OutputPath = "Code", Expression = "   ", CanonicalField = "SupplierItemCode" },
                },
            },
        };

        var rows = Rows(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");
    }

    [Fact]
    public void NoExpression_SourceMap_FastPath_Unchanged()
    {
        // A SourceMap with no Expression and no token still resolves exactly as before (pass-through).
        var lineRow = new Dictionary<string, string>
        {
            ["SupplierItemCode"] = "SUP-9",
            ["Quantity"]         = "1",
            ["UnitPrice"]        = "1",
        };
        var ov = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["Description"] = new SourceFieldRule { FixedValue = "Fixed Desc" },
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(lineRow, ov, Array.Empty<SourceToken>());

        result["SupplierItemCode"].Should().Be("SUP-9");   // untouched
        result["Description"].Should().Be("Fixed Desc");   // the explicit fixed-value rule
    }
}
