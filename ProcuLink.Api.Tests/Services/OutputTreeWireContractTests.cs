using System.Text.Json;
using System.Text.Json.Serialization;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase B/C wire contract: the OutputNode tree the FRONTEND posts uses camelCase string node types
/// ("object"/"array"/"field"/"attribute"). The override reader must deserialize them (the serializer
/// gained a JsonStringEnumConverter). Without it the enum would only accept numbers and the tree would
/// silently fail to load.
/// </summary>
public class OutputTreeWireContractTests
{
    [Fact]
    public void Read_OutputTree_WithFrontendStringEnums_Deserializes()
    {
        // Exactly the shape the frontend POSTs (camelCase props + STRING node types).
        const string json = """
        { "mappingOverride": {
            "outputTree": {
              "format": "json",
              "root": {
                "name": "root", "nodeType": "object", "children": [
                  { "name": "orderNumber", "nodeType": "field",
                    "rule": { "outputPath": "orderNumber", "canonicalField": "PoNumber" } },
                  { "name": "items", "nodeType": "array", "children": [
                    { "name": "item", "nodeType": "object", "children": [
                      { "name": "code", "nodeType": "field",
                        "rule": { "outputPath": "code", "canonicalField": "SupplierItemCode" } }
                    ] }
                  ] }
                ]
              }
            }
        } }
        """;

        using var doc = JsonDocument.Parse(json);
        var ov = OrderMappingOverrideReader.Read(doc);

        Assert.NotNull(ov);
        Assert.NotNull(ov!.OutputTree);
        Assert.Equal(OutputFormat.Json, ov.OutputTree!.Format);
        Assert.Equal(OutputNodeType.Object, ov.OutputTree.Root.NodeType);

        var children = ov.OutputTree.Root.Children;
        Assert.Equal(OutputNodeType.Field, children[0].NodeType);
        Assert.Equal("PoNumber", children[0].Rule!.CanonicalField);
        Assert.Equal(OutputNodeType.Array, children[1].NodeType);
        Assert.Equal(OutputNodeType.Object, children[1].Children[0].NodeType);
        Assert.Equal(OutputNodeType.Field, children[1].Children[0].Children[0].NodeType);
    }

    [Fact]
    public void Serialize_OutputTree_EmitsCamelCaseStringEnums_ForFrontend()
    {
        // The infer endpoint serializes its response with these options — the FE must receive STRING
        // node types ("object"/"array"/"field") + "json" format, not numbers.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("po", new OutputFieldRule { OutputPath = "po", CanonicalField = "PoNumber" }),
                OutputNode.Arr("lines", OutputNode.Obj("line",
                    OutputNode.FieldOf("sku", new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" })))),
        };
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var json = JsonSerializer.Serialize(tree, opts);

        Assert.Contains("\"nodeType\":\"object\"", json);
        Assert.Contains("\"nodeType\":\"array\"", json);
        Assert.Contains("\"nodeType\":\"field\"", json);
        Assert.Contains("\"format\":\"json\"", json);
        Assert.DoesNotContain("\"nodeType\":0", json);   // never raw enum numbers
    }
}
