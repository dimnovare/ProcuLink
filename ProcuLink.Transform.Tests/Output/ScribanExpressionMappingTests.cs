using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for the per-field Scriban <c>Expression</c> support (heart-piece-flex flexible mapping):
///   • the sandboxed <see cref="ScribanFieldEvaluator"/> itself (scope, arithmetic, interpolation,
///     invalid-expression safety, unknown-member relaxation);
///   • the <see cref="OutputFieldRule.Expression"/> wiring in the output tree
///     (precedence over canonicalField/fixedValue, chain-after-expression, header vs line scope);
///   • the additive invariant: with NO Expression set, output is byte-for-byte identical to before.
/// </summary>
public class ScribanExpressionMappingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

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
        var output = new OutputMappingConfig
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
        };

        var rows = Rows(EmitterRows.FlatCsv(output));

        rows[0].Should().Be("Ref,Code");
        rows[1].Should().Be("ORD-PO-9001,SUP-1");
    }

    [Fact]
    public void Output_LineExpression_Arithmetic_ComputesLineTotal()
    {
        var output = new OutputMappingConfig
        {
            Lines =
            {
                ["total"] = new OutputFieldRule
                {
                    OutputPath = "Total",
                    Expression = "{{ line.Quantity * line.UnitPrice }}",
                },
            },
        };

        var rows = Rows(EmitterRows.FlatCsv(output));

        rows[0].Should().Be("Total");
        rows[1].Should().Be("30");    // 3 * 10 (decimal scale 0)
        rows[2].Should().Be("11.0");  // 2 * 5.5 (decimal preserves scale → 11.0)
    }

    [Fact]
    public void Output_Expression_ThenManipulatorChain_AppliesInOrder()
    {
        var output = new OutputMappingConfig
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
        };

        var rows = Rows(EmitterRows.FlatCsv(output));

        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");
        rows[2].Should().Be("SUP-2");
    }

    [Fact]
    public void Output_HeaderExpression_InJson()
    {
        var output = new OutputMappingConfig
        {
            Header = { ["ref"] = new OutputFieldRule { OutputPath = "orderRef", Expression = "{{ order.PoNumber }}/{{ order.Currency }}" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "itemCode", CanonicalField = "SupplierItemCode" } },
        };

        // The same {header, lines} shape, drawn as a tree.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.Obj("header", OutputNode.FieldOf("orderRef", output.Header["ref"])),
                OutputNode.Arr("lines", OutputNode.Obj("line", OutputNode.FieldOf("itemCode", output.Lines["code"])))),
        };

        using var doc = ReadJson(EmitterRows.Emit(tree, EmitterRows.Po9001Header(), EmitterRows.Po9001Lines()));
        doc.RootElement.GetProperty("header").GetProperty("orderRef").GetString().Should().Be("PO-9001/EUR");
    }

    [Fact]
    public void Output_InvalidExpression_FallsBackToCanonicalField_DoesNotCrash()
    {
        var output = new OutputMappingConfig
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
        };

        var act = () => EmitterRows.FlatCsv(output);
        var result = act.Should().NotThrow().Subject;

        var rows = Rows(result);
        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");   // fell back to the canonical field
    }

    // ── F-1: Scriban indexer access to a bound source field via order["src::…"] ─────────────────

    [Fact]
    public void Output_ScribanExpression_ReadsBoundSourceToken_ViaIndexer()
    {
        var header = EmitterRows.Po9001Header();
        header["src::cell:r1c9"] = "EE777";

        var output = new OutputMappingConfig
        {
            // The src:: key is a legal ScriptObject member accessed by the indexer form.
            Header = { ["vat"] = new OutputFieldRule { OutputPath = "Vat", Expression = "VAT:{{ order[\"src::cell:r1c9\"] }}" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
        };

        var rows = Rows(EmitterRows.FlatCsv(output, header, EmitterRows.Po9001Lines()));
        rows[0].Should().Be("Vat,Code");
        rows[1].Should().Be("VAT:EE777,SUP-1");
    }

    [Fact]
    public void Output_ScribanExpression_MissingSrcKey_RendersEmpty_FailOpen()
    {
        // src::missing is absent from the bag.

        var output = new OutputMappingConfig
        {
            Header = { ["x"] = new OutputFieldRule { OutputPath = "X", Expression = "[{{ order[\"src::missing\"] }}]" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" } },
        };

        var rows = Rows(EmitterRows.FlatCsv(output));
        rows[0].Should().Be("X,Code");
        rows[1].Should().Be("[],SUP-1");   // relaxed member access → empty, never throws
    }

    // ── Additive invariant: no Expression → byte-for-byte unchanged ─────────────

    [Fact]
    public void NoExpression_Csv_IsByteForByteIdenticalToCanonicalFieldMapping()
    {
        // Identical rules, one WITHOUT Expression, one WITH a NULL Expression — must match.
        var withoutExpr = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber" } },
            Lines  =
            {
                ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                ["qty"]  = new OutputFieldRule { OutputPath = "Qty",  CanonicalField = "Quantity" },
            },
        };
        var withNullExpr = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber", Expression = null } },
            Lines  =
            {
                ["code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode", Expression = null },
                ["qty"]  = new OutputFieldRule { OutputPath = "Qty",  CanonicalField = "Quantity", Expression = null },
            },
        };

        var a = ReadCsv(EmitterRows.FlatCsv(withoutExpr));
        var b = ReadCsv(EmitterRows.FlatCsv(withNullExpr));

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
        var output = new OutputMappingConfig
        {
            Lines =
            {
                // Whitespace-only expression must be treated as "no expression".
                ["code"] = new OutputFieldRule { OutputPath = "Code", Expression = "   ", CanonicalField = "SupplierItemCode" },
            },
        };

        var rows = Rows(EmitterRows.FlatCsv(output));
        rows[0].Should().Be("Code");
        rows[1].Should().Be("SUP-1");
    }

}
