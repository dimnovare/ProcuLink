using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// B12 TASK 1 — byte-parity characterization lock. Pins the EXACT current emitter bytes so the
/// prefix-aware XML refactor (Task 3) cannot silently change formatting for non-namespaced trees.
/// </summary>
public class OutputTemplateEmitterByteParityTests
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

    // Representative non-namespaced trees for each emitter family.
    private static OutputNodeTemplate JsonTree() => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("orderNumber", Canon("PoNumber")),
            OutputNode.Obj("meta", OutputNode.FieldOf("currency", Canon("Currency"))),
            OutputNode.Arr("items", OutputNode.Obj("item",
                OutputNode.FieldOf("sku", Canon("SupplierItemCode")),
                OutputNode.FieldOf("qty", Canon("Quantity"))))),
    };

    private static OutputNodeTemplate XmlTree() => new()
    {
        Format = OutputFormat.Xml,
        Root = OutputNode.Obj("Order",
            OutputNode.Attr("ref", Canon("PoNumber")),
            OutputNode.Obj("Header", OutputNode.FieldOf("Currency", Canon("Currency"))),
            OutputNode.Arr("Lines", OutputNode.Obj("Line",
                OutputNode.Attr("sku", Canon("SupplierItemCode")),
                OutputNode.FieldOf("Qty", Canon("Quantity"))))),
    };

    private static OutputNodeTemplate XmlRootNsTree() => new()
    {
        Format = OutputFormat.Xml,
        Namespaces = new Dictionary<string, string>
        {
            ["cac"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
            ["cbc"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
        },
        Root = OutputNode.Obj("Order",
            OutputNode.FieldOf("ID", Canon("PoNumber")),
            OutputNode.Arr("Lines", OutputNode.Obj("Line",
                OutputNode.FieldOf("Qty", Canon("Quantity"))))),
    };

    private static OutputNodeTemplate CsvTree()
    {
        var config = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
            Lines =
            {
                ["sku"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                ["qty"] = new OutputFieldRule { OutputPath = "Qty", CanonicalField = "Quantity" },
            },
        };
        return OutputNodeTemplateConverter.FromFlat(config, OutputFormat.Csv);
    }

    private static string Emit(OutputNodeTemplate t)
    {
        var r = new OutputTemplateEmitter().Emit(t, ResolvedOrder(), new OrderMappingOverride());
        r.Content.Position = 0;
        using var sr = new StreamReader(r.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // Line-ending-normalized: the emitters emit CRLF (XmlWriter hardcodes it; AppendLine/Utf8JsonWriter
    // follow the runtime) which differs Windows vs Linux CI. Task 3 changes element WRITING, never
    // newlines — so normalizing locks structure/indentation/content exactly without a cross-OS flake.
    private static string N(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    [Fact]
    public void Json_NonNamespaced_Tree_Is_Unchanged()
    {
        const string expected = """
        {
          "orderNumber": "PO-1",
          "meta": {
            "currency": "EUR"
          },
          "items": [
            {
              "sku": "S-1",
              "qty": "3"
            },
            {
              "sku": "S-2",
              "qty": "2"
            }
          ]
        }
        """;
        Assert.Equal(N(expected), N(Emit(JsonTree())));
    }

    [Fact]
    public void Xml_NonNamespaced_Tree_Has_No_Xmlns_And_Stable_Bytes()
    {
        const string expected = """
        <?xml version="1.0" encoding="utf-8"?>
        <Order ref="PO-1">
          <Header>
            <Currency>EUR</Currency>
          </Header>
          <Lines>
            <Line sku="S-1">
              <Qty>3</Qty>
            </Line>
            <Line sku="S-2">
              <Qty>2</Qty>
            </Line>
          </Lines>
        </Order>
        """;
        var actual = Emit(XmlTree());
        Assert.DoesNotContain("xmlns", actual);          // non-namespaced trees emit NO xmlns
        Assert.Equal(N(expected), N(actual));
    }

    [Fact]
    public void Xml_With_Root_Namespaces_Map_Today()
    {
        // Locks the LEGACY root-level Namespaces mode (declared via WriteAttributeString("xmlns",...)).
        const string expected = """
        <?xml version="1.0" encoding="utf-8"?>
        <Order xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <ID>PO-1</ID>
          <Lines>
            <Line>
              <Qty>3</Qty>
            </Line>
            <Line>
              <Qty>2</Qty>
            </Line>
          </Lines>
        </Order>
        """;
        Assert.Equal(N(expected), N(Emit(XmlRootNsTree())));
    }

    [Fact]
    public void Csv_FlatTree_Is_Unchanged()
    {
        const string expected = "OrderRef,ItemCode,Qty\nPO-1,S-1,3\nPO-1,S-2,2\n";
        Assert.Equal(expected, N(Emit(CsvTree())));
    }
}
