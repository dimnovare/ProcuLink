using System.Text.Json;
using System.Text.Json.Serialization;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase B/C wire contract: the OutputNode tree the FRONTEND posts uses camelCase string node types
/// ("object"/"array"/"field"/"attribute"). Without string-enum support the enum would only accept
/// numbers and the tree would silently fail to load.
/// </summary>
public class OutputTreeWireContractTests
{
    [Fact]
    public void Bind_OutputNodeTemplate_FromBody_WithStringEnums_Deserializes()
    {
        // REGRESSION (live walkthrough 2026-06-15): the flow output endpoints take an
        // OutputNodeTemplate inside [FromBody] (FlowOutputsController's OutputRequest, PreviewRequest,
        // TestOutputRequest). MVC model binding uses the GLOBAL web JSON options, which have NO
        // string-enum converter. The frontend posts "nodeType":"object" / "format":"json".
        // Before the [JsonConverter] attributes on OutputNodeType + OutputNodeTemplate.Format, this
        // threw (enum-from-string) → HTTP 400 "Preview failed". These options replicate [FromBody]
        // EXACTLY — bare web defaults, nothing added — so the attributes are what must carry it.
        var webDefaults = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        const string body = """
        {
          "format": "json",
          "root": {
            "name": "root", "nodeType": "object", "children": [
              { "name": "orderNumber", "nodeType": "field",
                "rule": { "outputPath": "orderNumber", "canonicalField": "PoNumber" } },
              { "name": "lines", "nodeType": "array", "children": [
                { "name": "line", "nodeType": "object", "children": [
                  { "name": "code", "nodeType": "field",
                    "rule": { "outputPath": "code", "canonicalField": "SupplierItemCode" } },
                  { "name": "ref", "nodeType": "attribute",
                    "rule": { "outputPath": "ref", "fixedValue": "x" } }
                ] }
              ] }
            ]
          }
        }
        """;

        var tree = JsonSerializer.Deserialize<OutputNodeTemplate>(body, webDefaults);

        Assert.NotNull(tree);
        Assert.Equal(OutputFormat.Json, tree!.Format);
        Assert.Equal(OutputNodeType.Object, tree.Root.NodeType);
        var lines = tree.Root.Children[1];
        Assert.Equal(OutputNodeType.Array, lines.NodeType);
        Assert.Equal(OutputNodeType.Field, lines.Children[0].Children[0].NodeType);
        Assert.Equal(OutputNodeType.Attribute, lines.Children[0].Children[1].NodeType);
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
