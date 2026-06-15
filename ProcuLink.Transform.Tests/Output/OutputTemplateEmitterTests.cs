using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
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
    private static PurchaseOrderEntity ResolvedOrder()
    {
        var orderId = Guid.NewGuid();
        return new PurchaseOrderEntity
        {
            Id = orderId, OrgId = Guid.NewGuid(), SupplierId = Guid.NewGuid(),
            PoNumber = "PO-1", Currency = "EUR", OrderDate = new DateOnly(2026, 6, 15),
            Lines =
            {
                new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    SupplierItemCode = "S-1", Quantity = 3m, UnitPrice = 10m, NeedsReview = false },
                new PurchaseOrderLineEntity { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    SupplierItemCode = "S-2", Quantity = 2m, UnitPrice = 5m, NeedsReview = false },
            },
        };
    }

    private static OutputFieldRule Canon(string field) => new() { OutputPath = field, CanonicalField = field };

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

        var result = new OutputTemplateEmitter().Emit(template, ResolvedOrder(), new OrderMappingOverride());
        var json = ReadAll(result);
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

        var result = new OutputTemplateEmitter().Emit(template, ResolvedOrder(), new OrderMappingOverride());
        var xml = XDocument.Parse(ReadAll(result));
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

    [Fact]
    public void Emit_UnresolvedOrder_ThrowsValidation_NeverEmitsBlindDocument()
    {
        var order = ResolvedOrder();
        order.Lines[0].SupplierItemCode = null;   // unresolved line
        order.Lines[0].NeedsReview = true;

        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root", OutputNode.FieldOf("po", Canon("PoNumber"))),
        };

        Assert.Throws<TransformValidationException>(
            () => new OutputTemplateEmitter().Emit(template, order, new OrderMappingOverride()));
    }

    [Fact]
    public void Csv_ConvertedFromFlatConfig_IsByteIdenticalToFlatBuilder()
    {
        // BYTE-PARITY GATE: a flat OutputMappingConfig, lifted into an OutputNodeTemplate by the
        // converter and rendered by the new emitter, must produce the EXACT bytes the existing flat
        // CSV builder produces. This is what makes cutting existing suppliers over to the new engine
        // safe — same input → identical output.
        var order = ResolvedOrder();
        var config = new OutputMappingConfig
        {
            Header =
            {
                ["po"]  = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" },
                ["cur"] = new OutputFieldRule { OutputPath = "Currency", CanonicalField = "Currency" },
            },
            Lines =
            {
                ["sku"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                ["qty"] = new OutputFieldRule { OutputPath = "Qty",      CanonicalField = "Quantity" },
            },
        };
        var ov = new OrderMappingOverride { Output = config };

        var flatBytes = ReadBytes(new MappedTransformService().Build(order, ov, OutputFormat.Csv));
        var template  = OutputNodeTemplateConverter.FromFlat(config, OutputFormat.Csv);
        var treeBytes = ReadBytes(new OutputTemplateEmitter().Emit(template, order, ov));

        Assert.Equal(flatBytes, treeBytes);
    }

    private static string ReadAll(TransformResult result)
    {
        result.Content.Position = 0;
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static byte[] ReadBytes(TransformResult result)
    {
        result.Content.Position = 0;
        using var ms = new MemoryStream();
        result.Content.CopyTo(ms);
        return ms.ToArray();
    }
}
