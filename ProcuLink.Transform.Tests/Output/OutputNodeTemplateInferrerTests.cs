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

    [Fact]
    public void Csv_InfersColumns_AndBindsHeaderyColumnToPoNumber()
    {
        const string sample = "OrderRef,ItemCode,Qty\nPO-1,S-1,3\n";
        var t = OutputNodeTemplateInferrer.FromSample(sample, OutputFormat.Csv);

        Assert.Equal(OutputFormat.Csv, t.Format);
        var arr = Assert.Single(t.Root.Children.Where(c => c.NodeType == OutputNodeType.Array));
        var fields = arr.Children[0].Children.ToDictionary(c => c.Name);
        Assert.Equal("PoNumber", fields["OrderRef"].Rule!.CanonicalField);          // header-y column → PoNumber
        Assert.Equal("SupplierItemCode", fields["ItemCode"].Rule!.CanonicalField);
        Assert.Equal("Quantity", fields["Qty"].Rule!.CanonicalField);
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
