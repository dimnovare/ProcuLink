using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// B12 TASK 4 — pasting a real namespaced UBL sample infers a tree that round-trips to VALID namespaced
/// UBL (the offer⇔works gate): root default namespace + cbc:/cac: prefixes captured, unwrapped repeated
/// cac:OrderLine. Non-namespaced samples infer null namespaces (byte-identical legacy emit).
/// </summary>
public class OutputNodeTemplateInferrerNamespaceTests
{
    private const string ORDER2 = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private const string CBC = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string CAC = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Order2Ns = ORDER2, CbcNs = CBC, CacNs = CAC;

    private const string UblSample = """
    <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
           xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
           xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
      <cbc:ID>PO-SAMPLE</cbc:ID>
      <cac:OrderLine><cac:LineItem><cbc:Quantity>9</cbc:Quantity></cac:LineItem></cac:OrderLine>
      <cac:OrderLine><cac:LineItem><cbc:Quantity>9</cbc:Quantity></cac:LineItem></cac:OrderLine>
    </Order>
    """;

    private static PurchaseOrderEntity OrderWith(params decimal[] qtys)
    {
        var id = Guid.NewGuid();
        var o = new PurchaseOrderEntity { Id = id, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15) };
        var n = 1;
        foreach (var q in qtys)
            o.Lines.Add(new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = id, LineNumber = n++,
                SupplierItemCode = "S-" + n, Quantity = q, UnitPrice = 1m, NeedsReview = false });
        return o;
    }

    private static string Emit(OutputNodeTemplate t, PurchaseOrderEntity o)
    {
        var r = new OutputTemplateEmitter().Emit(t, o, new OrderMappingOverride());
        r.Content.Position = 0;
        using var sr = new StreamReader(r.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Infer_Ubl_Captures_Default_And_cac_cbc_Prefixes()
    {
        // Inferred as generic XML (the emitter renders namespaced XML; cXML/UBL formats are refused).
        var t = OutputNodeTemplateInferrer.FromSample(UblSample, OutputFormat.Xml);

        Assert.Equal(ORDER2, t.Root.Namespace);     // root default namespace
        Assert.Null(t.Root.Prefix);

        var id = t.Root.Children.First(c => c.Name == "ID");
        Assert.Equal("cbc", id.Prefix);
        Assert.Equal(CBC, id.Namespace);

        // Repeated cac:OrderLine → UNWRAPPED array (empty name); the item carries the cac namespace.
        var arr = t.Root.Children.First(c => c.NodeType == OutputNodeType.Array);
        Assert.Equal("", arr.Name);
        var orderLine = arr.Children.Single();
        Assert.Equal("cac", orderLine.Prefix);
        Assert.Equal(CAC, orderLine.Namespace);
    }

    [Fact]
    public void Infer_Then_Emit_Ubl_RoundTrips_Valid_Namespaced_Xml()
    {
        var t = OutputNodeTemplateInferrer.FromSample(UblSample, OutputFormat.Xml);
        var xml = Emit(t, OrderWith(3m, 5m));     // two real lines

        var doc = XDocument.Parse(xml);           // must be valid namespaced XML
        Assert.Equal(Order2Ns + "Order", doc.Root!.Name);

        // Two UNWRAPPED cac:OrderLine siblings (one per order line), NOT a double-nested wrapper.
        var lines = doc.Root.Elements(CacNs + "OrderLine").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("3", lines[0].Element(CacNs + "LineItem")!.Element(CbcNs + "Quantity")!.Value);
        Assert.Equal("5", lines[1].Element(CacNs + "LineItem")!.Element(CbcNs + "Quantity")!.Value);
        Assert.DoesNotContain("p1:", xml);        // real prefixes, not writer-invented
    }

    [Fact]
    public void Infer_NonNamespaced_Xml_Sets_Null_Namespace_And_Emits_No_Xmlns()
    {
        const string plain = "<Order ref=\"PO-1\"><Header><Currency>EUR</Currency></Header></Order>";
        var t = OutputNodeTemplateInferrer.FromSample(plain, OutputFormat.Xml);

        Assert.Null(t.Root.Namespace);
        Assert.Null(t.Root.Prefix);
        Assert.All(t.Root.Children, c => Assert.Null(c.Namespace));

        Assert.DoesNotContain("xmlns", Emit(t, OrderWith(1m)));   // byte-identical legacy path
    }
}
