using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// B12 adversarial-review fixes (2026-06-16): (A) repeated HEADER elements are no longer fanned out
/// per order line; (B) the tree emitter refuses cXML/UBL (no valid envelope from a generic tree);
/// (D) a no-namespace node never inherits an ancestor default namespace + a prefix without a namespace
/// fails loud.
/// </summary>
public class OutputTemplateB12ReviewFixesTests
{
    private static PurchaseOrderEntity OrderWith(int lines)
    {
        var id = Guid.NewGuid();
        var o = new PurchaseOrderEntity { Id = id, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15) };
        for (var n = 1; n <= lines; n++)
            o.Lines.Add(new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = id, LineNumber = n,
                SupplierItemCode = "S-" + n, Quantity = n, UnitPrice = 1m, NeedsReview = false });
        return o;
    }

    private static string Emit(OutputNodeTemplate t, PurchaseOrderEntity o)
    {
        var r = new OutputTemplateEmitter().Emit(t, o, new OrderMappingOverride());
        r.Content.Position = 0;
        using var sr = new StreamReader(r.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // ── Fix A: repeated header elements are NOT collapsed into a second per-line array ──────────────

    [Fact]
    public void Infer_RepeatedHeaderElements_AreSiblings_NotFannedPerLine()
    {
        const string sample = """
        <Order>
          <Note>Deliver to dock 5</Note>
          <Note>Fragile</Note>
          <Line><Qty>1</Qty></Line>
          <Line><Qty>1</Qty></Line>
        </Order>
        """;
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Xml);

        // EXACTLY ONE per-line array (the <Line> group), NOT two.
        Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Array && c.Collection == "lines");
        // The two <Note> header elements are preserved as their own sibling nodes (not an array).
        Assert.Equal(2, t.Root.Children.Count(c => c.Name == "Note"));

        // Emit against a 3-LINE order: 2 Notes (header, once each) + 3 Lines (one per order line).
        var doc = XDocument.Parse(Emit(t, OrderWith(3)));
        Assert.Equal(2, doc.Root!.Elements("Note").Count());   // header count unchanged, NOT 3
        Assert.Equal(3, doc.Root.Elements("Line").Count());    // line group fans per order line
    }

    [Fact]
    public void Infer_PicksLineGroupByName_EvenWhenItIsNotLast()
    {
        // Lines appear BEFORE the repeated header notes — name heuristic must still pick <Line>.
        const string sample = "<Order><Line><Qty>1</Qty></Line><Line><Qty>1</Qty></Line><Note>x</Note><Note>y</Note></Order>";
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Xml);

        var arr = Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Array);
        Assert.Equal("Line", arr.Children.Single().Name);
        Assert.Equal(2, t.Root.Children.Count(c => c.Name == "Note"));
    }

    // ── Fix B: cXML / UBL are refused (offer⇔works — no valid envelope from a generic tree) ─────────

    [Theory]
    [InlineData(OutputFormat.CXml)]
    [InlineData(OutputFormat.Ubl)]
    public void Emit_CxmlOrUbl_Tree_FailsLoud(OutputFormat format)
    {
        var t = new OutputNodeTemplate
        {
            Format = format,
            Root = OutputNode.Obj("order", OutputNode.FieldOf("po", new OutputFieldRule { OutputPath = "po", CanonicalField = "PoNumber" })),
        };
        var ex = Assert.Throws<ArgumentException>(() => Emit(t, OrderWith(1)));
        Assert.Contains(format.ToString(), ex.Message);
    }

    // ── Fix D: namespace hardening ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Emit_NoNamespaceChild_UnderDefaultNsParent_DoesNotInheritTheDefault()
    {
        const string OTHER = "urn:example:OTHER";
        var t = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = new OutputNode { Name = "Root", NodeType = OutputNodeType.Object, Namespace = OTHER,
                Children = { new OutputNode { Name = "Plain", NodeType = OutputNodeType.Field,
                    Rule = new OutputFieldRule { OutputPath = "Plain", CanonicalField = "PoNumber" } } } },
        };
        var doc = XDocument.Parse(Emit(t, OrderWith(1)));
        Assert.Equal(XNamespace.Get(OTHER) + "Root", doc.Root!.Name);
        // The no-namespace child must be in NO namespace — never silently in the parent default.
        Assert.Equal("", doc.Root.Elements().First().Name.NamespaceName);
    }

    [Fact]
    public void Emit_PrefixWithoutNamespace_FailsLoud()
    {
        var t = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = new OutputNode { Name = "Order", NodeType = OutputNodeType.Object,
                Children = { new OutputNode { Name = "ID", NodeType = OutputNodeType.Field, Prefix = "cbc", /* Namespace null */
                    Rule = new OutputFieldRule { OutputPath = "ID", CanonicalField = "PoNumber" } } } },
        };
        Assert.Throws<ArgumentException>(() => Emit(t, OrderWith(1)));
    }
}
