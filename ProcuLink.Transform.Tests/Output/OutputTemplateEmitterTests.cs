using System.Text.Json;
using System.Xml.Linq;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Phase B — proves the OutputTemplate emitter renders ARBITRARY structure (nesting, repeating
/// groups, attributes, custom names) that the flat {header, lines} builder cannot. Values resolve
/// through the SAME machinery the flat builder uses, so only the structure is new.
/// </summary>
public class OutputTemplateEmitterTests
{
    private static OutputFieldRule Canon(string field) => new() { OutputPath = field, CanonicalField = field };

    /// <summary>The shared rows with line 2's quantity at 0.</summary>
    private static TransformResult ZeroQuantitySecondLine(OutputNodeTemplate template) =>
        EmitterRows.Emit(template, EmitterRows.Header(), EmitterRows.Line(1, "S-1", 3m, 10m), EmitterRows.Line(2, "S-2", 0m, 5m));

    [Fact]
    public void Json_RendersNestedObjects_RepeatingArray_AndRenamedKeys()
    {
        // {"orderNumber":"PO-1","meta":{"currency":"EUR"},"items":[{"sku":"S-1","qty":"3"},{"sku":"S-2","qty":"2"}]}
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("orderNumber", Canon("PoNumber")),          // renamed leaf
                OutputNode.Obj("meta",                                          // NESTING (impossible in flat builder)
                    OutputNode.FieldOf("currency", Canon("Currency"))),
                OutputNode.Arr("items",                                         // repeating group of OBJECTS
                    OutputNode.Obj("item",
                        OutputNode.FieldOf("sku", Canon("SupplierItemCode")),
                        OutputNode.FieldOf("qty", Canon("Quantity"))))),
        };

        var json = EmitterRows.Emit(template).Text();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("PO-1", root.GetProperty("orderNumber").GetString());
        Assert.Equal("EUR", root.GetProperty("meta").GetProperty("currency").GetString());
        var items = root.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("S-1", items[0].GetProperty("sku").GetString());
        Assert.Equal("3", items[0].GetProperty("qty").GetString());
        Assert.Equal("S-2", items[1].GetProperty("sku").GetString());
        Assert.Equal("2", items[1].GetProperty("qty").GetString());
    }

    [Fact]
    public void Xml_RendersAttributes_NestedElements_AndRepeatingLines()
    {
        // <Order ref="PO-1"><Header><Currency>EUR</Currency></Header>
        //   <Lines><Line sku="S-1"><Qty>3</Qty></Line><Line sku="S-2"><Qty>2</Qty></Line></Lines></Order>
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = OutputNode.Obj("Order",
                OutputNode.Attr("ref", Canon("PoNumber")),                      // ATTRIBUTE (impossible today)
                OutputNode.Obj("Header",
                    OutputNode.FieldOf("Currency", Canon("Currency"))),
                OutputNode.Arr("Lines",
                    OutputNode.Obj("Line",
                        OutputNode.Attr("sku", Canon("SupplierItemCode")),      // per-line attribute
                        OutputNode.FieldOf("Qty", Canon("Quantity"))))),
        };

        var xml = XDocument.Parse(EmitterRows.Emit(template).Text());
        var order = xml.Root!;

        Assert.Equal("Order", order.Name.LocalName);
        Assert.Equal("PO-1", order.Attribute("ref")!.Value);
        Assert.Equal("EUR", order.Element("Header")!.Element("Currency")!.Value);
        var lines = order.Element("Lines")!.Elements("Line").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("S-1", lines[0].Attribute("sku")!.Value);
        Assert.Equal("3", lines[0].Element("Qty")!.Value);
        Assert.Equal("S-2", lines[1].Attribute("sku")!.Value);
        Assert.Equal("2", lines[1].Element("Qty")!.Value);
    }

    // ── Conditional fields (OutputNode.IncludeWhen) ────────────────────────────────

    [Fact]
    public void Json_IncludeWhen_OmitsField_WhenFalsy_KeepsField_WhenTruthy()
    {
        // A header field that only appears when the predicate holds. EUR order → currency present;
        // a "discount only when present" field with an always-false predicate is omitted.
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("po", Canon("PoNumber")),
                OutputNode.FieldOf("currency", Canon("Currency")) with { IncludeWhen = "order.Currency == \"EUR\"" },
                OutputNode.FieldOf("usdNote", Canon("Currency"))   with { IncludeWhen = "order.Currency == \"USD\"" }),
        };

        var json = EmitterRows.Emit(template).Text();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("PO-1", root.GetProperty("po").GetString());
        Assert.True(root.TryGetProperty("currency", out _));          // EUR predicate true → kept
        Assert.False(root.TryGetProperty("usdNote", out _));          // USD predicate false → omitted
    }

    [Fact]
    public void Json_IncludeWhen_OnArrayItem_SkipsLines_FailingThePredicate()
    {
        // The classic "drop zero-quantity lines" recipe. Line 1 qty 3, line 2 qty 0 → only line 1 emits.
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.Arr("items",
                    OutputNode.Obj("item",
                        OutputNode.FieldOf("sku", Canon("SupplierItemCode")),
                        OutputNode.FieldOf("qty", Canon("Quantity"))) with { IncludeWhen = "line.Quantity > 0" })),
        };

        var json = ZeroQuantitySecondLine(template).Text();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("S-1", items[0].GetProperty("sku").GetString());
    }

    [Fact]
    public void Xml_IncludeWhen_OmitsElementAndAttribute_WhenFalsy()
    {
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = OutputNode.Obj("Order",
                OutputNode.Attr("ref", Canon("PoNumber")),
                OutputNode.FieldOf("Currency", Canon("Currency")) with { IncludeWhen = "order.Currency == \"USD\"" },
                OutputNode.FieldOf("Po", Canon("PoNumber"))),
        };

        var xml = XDocument.Parse(EmitterRows.Emit(template).Text());
        var order = xml.Root!;

        Assert.Equal("PO-1", order.Attribute("ref")!.Value);
        Assert.Null(order.Element("Currency"));                     // USD predicate false → element omitted
        Assert.Equal("PO-1", order.Element("Po")!.Value);
    }

    [Fact]
    public void Csv_IncludeWhen_OnLineItem_DropsRows_FailingThePredicate()
    {
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Csv,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("OrderRef", Canon("PoNumber")),
                OutputNode.Arr("lines",
                    OutputNode.Obj("line",
                        OutputNode.FieldOf("ItemCode", Canon("SupplierItemCode")),
                        OutputNode.FieldOf("Qty", Canon("Quantity"))) with { IncludeWhen = "line.Quantity > 0" })),
        };

        var csv = ZeroQuantitySecondLine(template).Text();
        var rows = csv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("OrderRef,ItemCode,Qty", rows[0]);
        Assert.Equal(2, rows.Length);            // header + ONE data row (zero-qty line dropped)
        Assert.Contains("S-1", rows[1]);
    }

    [Fact]
    public void IncludeWhen_NullOrBlank_IsByteIdentical_NoBehaviourChange()
    {
        // Regression guard: a tree with IncludeWhen left null must render the EXACT bytes as before.
        OutputNode WithoutPredicate() => OutputNode.Obj("root",
            OutputNode.FieldOf("po", Canon("PoNumber")),
            OutputNode.Arr("items", OutputNode.Obj("item", OutputNode.FieldOf("sku", Canon("SupplierItemCode")))));

        var baseline = new OutputNodeTemplate { Format = OutputFormat.Json, Root = WithoutPredicate() };
        var withNull = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = WithoutPredicate() with { IncludeWhen = null },   // explicit null
        };

        var a = EmitterRows.Emit(baseline).Bytes();
        var b = EmitterRows.Emit(withNull).Bytes();
        Assert.Equal(a, b);
    }

    [Fact]
    public void IncludeWhen_BrokenPredicate_FailsOpen_KeepsTheNode()
    {
        // An authoring mistake must NEVER silently drop the supplier's data. A garbage predicate keeps it.
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("po", Canon("PoNumber")) with { IncludeWhen = "this is not valid (((" }),
        };

        var json = EmitterRows.Emit(template).Text();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PO-1", doc.RootElement.GetProperty("po").GetString());   // kept despite broken predicate
    }

    // ── F-1: an AST leaf bound to an arbitrary source field (src:: key OR typed SourceToken) ────────

    [Fact]
    public void Xml_AstField_BoundToTypedSourceToken_EmitsTokenValue()
    {
        var header = EmitterRows.Header();
        header["src::seg:RFF[1].el2"] = "REF-99";
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = OutputNode.Obj("Order",
                OutputNode.FieldOf("Ref", new OutputFieldRule { OutputPath = "Ref", SourceToken = "seg:RFF[1].el2" }),
                OutputNode.Arr("Lines", OutputNode.Obj("Line",
                    OutputNode.FieldOf("Sku", Canon("SupplierItemCode"))))),
        };

        var xml = XDocument.Parse(EmitterRows.Emit(template, header, EmitterRows.TwoLines()).Text());
        Assert.Equal("REF-99", xml.Root!.Element("Ref")!.Value);
    }
}
