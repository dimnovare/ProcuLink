using System.Text.Json;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-12 P0-1 / D3: <see cref="OutputTreeFormats.IsRenderable"/> and the emitter must answer the
/// SAME question, for EVERY <see cref="OutputFormat"/>. This file proves it by EXECUTING the emitter
/// for every enum member. (The order-side "usable output tree" predicate these facts were first
/// written against went with the order pipeline, plan 13b, 2026-11-13.)
/// </summary>
public class OutputTreeRenderabilityTests
{
    /// <summary>The shared header with one line, S-1 × 3.</summary>
    private static TransformResult Emit(OutputNodeTemplate tree) =>
        EmitterRows.Emit(tree, EmitterRows.Header(), EmitterRows.Line(1, "S-1", 3m, 10m));

    private static OutputFieldRule Canon(string field) => new() { OutputPath = field, CanonicalField = field };

    /// <summary>A perfectly ordinary drawn layout — header leaf + repeating line group. Nothing about
    /// it is malformed; the ONLY variable across these cases is the template's format.</summary>
    private static OutputNode DrawnRoot() => OutputNode.Obj("root",
        OutputNode.FieldOf("orderNumber", Canon("PoNumber")),
        OutputNode.Arr("items", OutputNode.Obj("item",
            OutputNode.FieldOf("sku", Canon("SupplierItemCode")))));

    /// <summary>Executes the emitter and reports whether it produced a document at all.</summary>
    private static bool EmitterRenders(OutputFormat format)
    {
        try
        {
            Emit(new OutputNodeTemplate { Format = format, Root = DrawnRoot() });
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static TheoryData<OutputFormat> AllOutputFormats()
    {
        var data = new TheoryData<OutputFormat>();
        foreach (var format in Enum.GetValues<OutputFormat>()) data.Add(format);
        return data;
    }

    // ══ P0-1 — the declared renderable set must never claim a layout the emitter will refuse ═════

    [Theory]
    [MemberData(nameof(AllOutputFormats))]
    public void TheDeclaredRenderableSet_AndTheEmitter_AgreeForEveryOutputFormat(OutputFormat format)
    {
        // Walks the WHOLE enum, so a new OutputFormat member cannot be added without either teaching
        // the emitter to render it or declaring it not renderable.
        Assert.Equal(OutputTreeFormats.IsRenderable(format), EmitterRenders(format));
    }

    [Theory]
    [MemberData(nameof(AllOutputFormats))]
    public void NoFormatIsBothRenderableAndEnvelopeOnly(OutputFormat format)
    {
        // The two roles are exclusive: a format either renders its document from the tree, or (for
        // cXML/X12 only) reads envelope identity off it. Everything else does neither — which is
        // exactly why "not renderable" must never be treated as "envelope-bearing".
        Assert.False(OutputTreeFormats.IsRenderable(format) && OutputTreeFormats.ReadsEnvelopeIdentity(format));
    }

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public void TheEmitterReallyDoesRefuseThoseFormats(OutputFormat format)
    {
        // The other half of the pair: the assertion above is only meaningful because the emitter
        // genuinely throws here. Executed, not assumed.
        Assert.Throws<ArgumentException>(() => Emit(new OutputNodeTemplate { Format = format, Root = DrawnRoot() }));
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.Csv)]
    public void TheThreeRenderableFormatsStayUsable(OutputFormat format)
    {
        // Assert the DIFFERENCE: "refuse everything" would pass every test above. The three formats
        // the emitter really renders must keep rendering.
        Assert.True(EmitterRenders(format));
    }

    // ══ D3 — a children list holding a null element ══════════════════════════════════════════════

    [Fact]
    public void TheEmitterSkipsANullChild_InsteadOfThrowing()
    {
        // A null can sit anywhere in a deserialised children list. Emitting must not NullReference.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("orderNumber", Canon("PoNumber")),
                new OutputNode
                {
                    Name = "nested",
                    NodeType = OutputNodeType.Object,
                    Children = new List<OutputNode> { null! },
                }),
        };

        var result = Emit(tree);

        using var reader = new StreamReader(result.Content);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal("PO-1", doc.RootElement.GetProperty("orderNumber").GetString());
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.Csv)]
    public void AHeaderOnlyTree_WithNoRepeatingGroup_StillEmits(OutputFormat format)
    {
        // The simplest layout an operator can draw — one header column, no line group. The CSV emitter
        // reaches for the line-item template's children, and a null-guard whose `?:` bound looser than
        // its `??` turned "no line item" into a null list that NullReferenced on the next line.
        var tree = new OutputNodeTemplate
        {
            Format = format,
            Root = OutputNode.Obj("root", OutputNode.FieldOf("orderNumber", Canon("PoNumber"))),
        };

        var result = Emit(tree);

        using var reader = new StreamReader(result.Content);
        Assert.Contains("orderNumber", reader.ReadToEnd());
    }
}
