using System.Text.Json;
using System.Text.Json.Serialization;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// B12 TASK 2 — the additive per-node <c>Namespace</c>/<c>Prefix</c> on <see cref="OutputNode"/>.
/// They default null (so existing trees + JSON/CSV emit are byte-identical) and round-trip through the
/// same camelCase JSON the override persistence uses (the contract the inferrer + a future FE rely on).
/// </summary>
public class OutputNodeNamespaceModelTests
{
    [Fact]
    public void NewNamespaceFields_DefaultNull()
    {
        Assert.Null(new OutputNode().Namespace);
        Assert.Null(new OutputNode().Prefix);
        // The convenience factories must keep producing null-namespace nodes (byte-identity guard).
        Assert.Null(OutputNode.Obj("x").Namespace);
        Assert.Null(OutputNode.Arr("xs", OutputNode.Obj("x")).Prefix);
        Assert.Null(OutputNode.FieldOf("f", new OutputFieldRule { FixedValue = "" }).Namespace);
        Assert.Null(OutputNode.Attr("a", new OutputFieldRule { FixedValue = "" }).Prefix);
    }

    [Fact]
    public void Template_RoundTrips_PerNode_Namespace_Through_CamelCaseJson()
    {
        const string CBC = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Ubl,
            Root = new OutputNode
            {
                Name = "Order", NodeType = OutputNodeType.Object,
                Namespace = "urn:oasis:names:specification:ubl:schema:xsd:Order-2",
                Children =
                {
                    new OutputNode { Name = "ID", NodeType = OutputNodeType.Field, Prefix = "cbc", Namespace = CBC,
                        Rule = new OutputFieldRule { OutputPath = "ID", CanonicalField = "PoNumber" } },
                },
            },
        };

        // Same options shape the override reader/service use for canonical_json.
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        var json = JsonSerializer.Serialize(template, opts);
        Assert.Contains("\"namespace\":", json);   // camelCase wire names
        Assert.Contains("\"prefix\":\"cbc\"", json);

        var back = JsonSerializer.Deserialize<OutputNodeTemplate>(json, opts)!;
        Assert.Equal("urn:oasis:names:specification:ubl:schema:xsd:Order-2", back.Root.Namespace);
        var id = Assert.Single(back.Root.Children);
        Assert.Equal("cbc", id.Prefix);
        Assert.Equal(CBC, id.Namespace);
    }
}
