using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Phase D — pasting the supplier's required sample infers a matching OutputNode tree (deterministic,
/// no AI). Proves the structure is recovered and leaves are pre-bound to canonical fields by name.
/// </summary>
public class OutputNodeTemplateInferrerTests
{
    [Fact]
    public void Json_InfersNestedStructure_AndPreBindsByName()
    {
        const string sample = """
        {"orderNumber":"PO-1","meta":{"currency":"EUR"},"items":[{"sku":"S-1","qty":3}]}
        """;
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json);

        Assert.Equal(OutputFormat.Json, t.Format);
        Assert.Equal(OutputNodeType.Object, t.Root.NodeType);
        var byName = t.Root.Children.ToDictionary(c => c.Name);

        Assert.Equal(OutputNodeType.Field, byName["orderNumber"].NodeType);
        Assert.Equal("PoNumber", byName["orderNumber"].Rule!.CanonicalField);

        Assert.Equal(OutputNodeType.Object, byName["meta"].NodeType);               // nesting recovered
        Assert.Equal("Currency", byName["meta"].Children[0].Rule!.CanonicalField);

        var items = byName["items"];
        Assert.Equal(OutputNodeType.Array, items.NodeType);                          // repeating group recovered
        Assert.Equal("lines", items.Collection);
        var item = items.Children[0];
        var itemByName = item.Children.ToDictionary(c => c.Name);
        Assert.Equal("SupplierItemCode", itemByName["sku"].Rule!.CanonicalField);
        Assert.Equal("Quantity", itemByName["qty"].Rule!.CanonicalField);
    }

    [Theory]
    [InlineData("poRef")]
    [InlineData("PO Ref")]
    [InlineData("PO No")]
    [InlineData("PONr")]
    [InlineData("orderNo")]
    public void Json_PreBinds_CommonPoNumberAliases(string poFieldName)
    {
        // Live prod walkthrough (2026-06-16): a supplier sample keyed "poRef" left the PO id unbound.
        // Common PO-number aliases must pre-bind to PoNumber so paste-sample lands close to correct.
        var sample = $"{{\"{poFieldName}\":\"PO-1\",\"items\":[{{\"sku\":\"S-1\"}}]}}";
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json);
        var field = Assert.Single(t.Root.Children, c => c.Name == poFieldName);
        Assert.Equal("PoNumber", field.Rule!.CanonicalField);
    }

    [Fact]
    public void Csv_InfersColumns_AndBindsHeaderyColumnToPoNumber()
    {
        const string sample = "OrderRef,ItemCode,Qty\nPO-1,S-1,3\n";
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Csv);

        Assert.Equal(OutputFormat.Csv, t.Format);
        var arr = Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Array);
        var fields = arr.Children[0].Children.ToDictionary(c => c.Name);
        Assert.Equal("PoNumber", fields["OrderRef"].Rule!.CanonicalField);          // header-y column → PoNumber
        Assert.Equal("SupplierItemCode", fields["ItemCode"].Rule!.CanonicalField);
        Assert.Equal("Quantity", fields["Qty"].Rule!.CanonicalField);
    }

    [Fact]
    public void Xml_InfersElements_Attributes_AndWrappedRepeatingGroup()
    {
        const string sample = """
        <Order orderRef="PO-1"><Header><Currency>EUR</Currency></Header><Lines><Line sku="S-1"><Qty>3</Qty></Line><Line sku="S-2"><Qty>2</Qty></Line></Lines></Order>
        """;
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Xml);

        Assert.Equal(OutputFormat.Xml, t.Format);
        Assert.Equal("Order", t.Root.Name);
        Assert.Equal(OutputNodeType.Object, t.Root.NodeType);

        // orderRef attribute → Attribute node bound to PoNumber by name
        var refAttr = Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Attribute);
        Assert.Equal("orderRef", refAttr.Name);
        Assert.Equal("PoNumber", refAttr.Rule!.CanonicalField);

        // Header object with a Currency field
        var header = Assert.Single(t.Root.Children, c => c.Name == "Header");
        Assert.Equal("Currency", header.Children[0].Name);

        // Lines wrapper → Array of Line items
        var lines = Assert.Single(t.Root.Children, c => c.Name == "Lines");
        Assert.Equal(OutputNodeType.Array, lines.NodeType);
        var lineItem = lines.Children[0];
        Assert.Equal("Line", lineItem.Name);
        Assert.Equal(OutputNodeType.Attribute, lineItem.Children.Single(c => c.Name == "sku").NodeType);
        Assert.Equal("SupplierItemCode", lineItem.Children.Single(c => c.Name == "sku").Rule!.CanonicalField);
        Assert.Equal("Quantity", lineItem.Children.Single(c => c.Name == "Qty").Rule!.CanonicalField);
    }

    // ── F-2: inferred-but-unmapped columns are UNBOUND (null), not a silent empty-string pill ──

    [Fact]
    public void Json_UnmappableColumn_InfersUnboundRule_NotEmptyFixedValue()
    {
        // EANCode / extraThing have no name match → they must come back UNBOUND (FixedValue == null,
        // CanonicalField == null) so the designer shows "pick a field" instead of a bound "" pill that
        // silently delivers empty forever.
        const string sample = """
        {"orderNumber":"PO-1","EANCode":"X","items":[{"sku":"S-1","extraThing":"Y"}]}
        """;
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json);

        var ean = Assert.Single(t.Root.Children, c => c.Name == "EANCode");
        Assert.Null(ean.Rule!.CanonicalField);
        Assert.Null(ean.Rule!.FixedValue);   // F-2: UNBOUND, not ""

        var item = Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Array).Children[0];
        var extra = Assert.Single(item.Children, c => c.Name == "extraThing");
        Assert.Null(extra.Rule!.CanonicalField);
        Assert.Null(extra.Rule!.FixedValue);

        // A MAPPABLE column is still bound (unchanged).
        var po = Assert.Single(t.Root.Children, c => c.Name == "orderNumber");
        Assert.Equal("PoNumber", po.Rule!.CanonicalField);
    }

    [Theory]
    [InlineData(OutputFormat.Csv, "OrderRef,EANCode\nPO-1,X\n")]
    public void Csv_UnmappableColumn_InfersUnboundRule(OutputFormat format, string sample)
    {
        var t = OutputNodeTemplateInferrer.FromSample(sample, format);
        var arr = Assert.Single(t.Root.Children, c => c.NodeType == OutputNodeType.Array);
        var ean = Assert.Single(arr.Children[0].Children, c => c.Name == "EANCode");
        Assert.Null(ean.Rule!.CanonicalField);
        Assert.Null(ean.Rule!.FixedValue);   // F-2
    }

    [Fact]
    public void ByteParity_UnboundInferredColumn_DeliversIdenticallyTo_ExplicitEmptyFixedValue()
    {
        // Characterization (byte-parity gate): the F-2 change ("" → null for inferred-unmapped) must
        // NOT change emitted bytes. An UNBOUND inferred column (FixedValue == null) and an EXPLICIT
        // empty fixed value (FixedValue == "") both deliver the same bytes — only the designer
        // binding-state differs.
        const string sample = """
        {"orderNumber":"X","EANCode":"X","items":[{"sku":"X","qty":0}]}
        """;
        var order = MakeResolvedOrder();

        // Inferred tree: EANCode comes back UNBOUND under F-2.
        var inferred = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json);
        var inferredBytes = Emit(inferred, order);

        // Same tree but EANCode explicitly set to FixedValue == "" (the pre-F-2 inferred shape, and
        // exactly what a user setting an empty constant produces).
        var explicitEmpty = WithEanFixedValue(OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json), "");
        var explicitBytes = Emit(explicitEmpty, order);

        Assert.Equal(explicitBytes, inferredBytes);   // identical delivered bytes

        // And the unbound column renders as an empty string in the JSON (not dropped, not crashing).
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(inferredBytes));
        Assert.Equal("", doc.RootElement.GetProperty("EANCode").GetString());
    }

    private static OutputNodeTemplate WithEanFixedValue(OutputNodeTemplate t, string value)
    {
        // OutputNode is an init-only record — swap the EANCode child in the (mutable) Children list
        // for a copy whose Rule carries an EXPLICIT empty fixed value.
        var idx = t.Root.Children.FindIndex(c => c.Name == "EANCode");
        var ean = t.Root.Children[idx];
        t.Root.Children[idx] = ean with
        {
            Rule = new OutputFieldRule { OutputPath = ean.Rule!.OutputPath, FixedValue = value },
        };
        return t;
    }

    private static PurchaseOrderEntity MakeResolvedOrder()
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id = orderId, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-42", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15),
            Lines = { new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                SupplierItemCode = "S-9", Quantity = 7m, UnitPrice = 1m, NeedsReview = false } },
        };
    }

    private static byte[] Emit(OutputNodeTemplate template, PurchaseOrderEntity order)
    {
        var result = new OutputTemplateEmitter().Emit(template, order, new OrderMappingOverride());
        result.Content.Position = 0;
        using var ms = new MemoryStream();
        result.Content.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Inferred_Json_RoundTrips_ThroughEmitter_WithSameShape()
    {
        // Infer a tree from a supplier sample, then render a real order through it: the OUTPUT keys
        // match the sample's keys (structure recovered + renders).
        const string sample = """
        {"orderNumber":"X","items":[{"sku":"X","qty":0}]}
        """;
        var template = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Json);

        var orderId = Guid.NewGuid();
        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-42", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15),
            Lines = { new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                SupplierItemCode = "S-9", Quantity = 7m, UnitPrice = 1m, NeedsReview = false } },
        };

        var result = new OutputTemplateEmitter().Emit(template, order, new OrderMappingOverride());
        result.Content.Position = 0;
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        using var doc = JsonDocument.Parse(sr.ReadToEnd());

        Assert.Equal("PO-42", doc.RootElement.GetProperty("orderNumber").GetString());   // bound to real PoNumber
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("S-9", items[0].GetProperty("sku").GetString());
        Assert.Equal("7", items[0].GetProperty("qty").GetString());
    }
}
