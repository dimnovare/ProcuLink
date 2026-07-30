using System.Text.Json;
using System.Text.Json.Serialization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-12 P0-1 / D3 / D4 — the "usable output tree" predicate and the emitter must answer the SAME
/// question, for EVERY <see cref="OutputFormat"/>.
///
/// <para>The predicate is what promote uses to tell the operator "saved the file layout you designed"
/// and what the transform uses to route the document to <see cref="OutputTemplateEmitter"/>. When it
/// says "usable" for a format the emitter refuses, one click of "Save this layout for the supplier"
/// bricks EVERY future order for that supplier — the emitter throws, the transform fails terminally,
/// and the promote reported success. So the two must be derived from ONE source of truth, and this
/// file is the lock that proves it by EXECUTING the emitter for every enum member.</para>
/// </summary>
public class OutputTreeRenderabilityTests
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
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    SupplierItemCode = "S-1", Quantity = 3m, UnitPrice = 10m, NeedsReview = false,
                },
            },
        };
    }

    private static OutputFieldRule Canon(string field) => new() { OutputPath = field, CanonicalField = field };

    /// <summary>A perfectly ordinary drawn layout — header leaf + repeating line group. Nothing about
    /// it is malformed; the ONLY variable across these cases is the template's format.</summary>
    private static OutputNode DrawnRoot() => OutputNode.Obj("root",
        OutputNode.FieldOf("orderNumber", Canon("PoNumber")),
        OutputNode.Arr("items", OutputNode.Obj("item",
            OutputNode.FieldOf("sku", Canon("SupplierItemCode")))));

    /// <summary>Executes the emitter and reports whether it produced a document at all.</summary>
    private static bool EmitterRenders(OutputFormat format)
    {
        try
        {
            new OutputTemplateEmitter().Emit(
                new OutputNodeTemplate { Format = format, Root = DrawnRoot() },
                ResolvedOrder(),
                new OrderMappingOverride());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static TheoryData<OutputFormat> AllOutputFormats()
    {
        var data = new TheoryData<OutputFormat>();
        foreach (var format in Enum.GetValues<OutputFormat>()) data.Add(format);
        return data;
    }

    // ══ P0-1 — the predicate must never claim a layout the emitter will refuse ═══════════════════

    [Theory]
    [MemberData(nameof(AllOutputFormats))]
    public void UsableOutputTree_AndTheEmitter_AgreeForEveryOutputFormat(OutputFormat format)
    {
        // Walks the WHOLE enum, so a new OutputFormat member cannot be added without either teaching
        // the emitter to render it or teaching the predicate to refuse it. "Not cXML/X12" was the
        // drift: it silently admitted Ubl, UblOrder, X12_850 and EdifactOrders.
        var tree = new OutputNodeTemplate { Format = format, Root = DrawnRoot() };

        Assert.Equal(
            EmitterRenders(format),
            OrderMappingOverrideReader.HasUsableOutputTree(tree));
    }

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public void ADrawnTreeInAFormatTheEmitterRefuses_IsNotAUsableLayout(OutputFormat format)
    {
        // The four formats the "not cXML/X12" predicate wrongly admitted. Promote answered "Saved …
        // the file layout you designed" for each, then every future order for that supplier died in
        // OutputTemplateEmitter.Emit with a terminal transform_failed.
        var tree = new OutputNodeTemplate { Format = format, Root = DrawnRoot() };

        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(tree));
    }

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public void TheEmitterReallyDoesRefuseThoseFormats(OutputFormat format)
    {
        // The other half of the pair: the assertion above is only meaningful because the emitter
        // genuinely throws here. Executed, not assumed.
        Assert.Throws<ArgumentException>(() => new OutputTemplateEmitter().Emit(
            new OutputNodeTemplate { Format = format, Root = DrawnRoot() },
            ResolvedOrder(),
            new OrderMappingOverride()));
    }

    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Xml)]
    [InlineData(OutputFormat.Csv)]
    public void TheThreeRenderableFormatsStayUsable(OutputFormat format)
    {
        // Assert the DIFFERENCE: "refuse everything" would pass every test above. The three formats
        // the emitter really renders must keep routing to it.
        var tree = new OutputNodeTemplate { Format = format, Root = DrawnRoot() };

        Assert.True(OrderMappingOverrideReader.HasUsableOutputTree(tree));
        Assert.True(EmitterRenders(format));
    }

    // ══ D3 — a children list holding a null element ══════════════════════════════════════════════

    private const string NullChildElementJson =
        """{"format":"json","root":{"name":"root","nodeType":"object","children":[null]}}""";

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void ChildrenHoldingOnlyANullElement_IsNotAUsableLayout()
    {
        // System.Text.Json puts a null INTO the list for `"children":[null]`, which is reachable from
        // the [FromBody] PUT that writes SupplierPoMapping.ConfigJson verbatim. `Count > 0` was true,
        // so the tree passed usability and then dereferenced the null inside the emitter.
        var tree = JsonSerializer.Deserialize<OutputNodeTemplate>(NullChildElementJson, ReaderOptions);

        Assert.NotNull(tree);
        Assert.Single(tree!.Root.Children);          // the null really is in the list
        Assert.Null(tree.Root.Children[0]);

        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(tree));
    }

    [Fact]
    public void TheEmitterSkipsANullChild_InsteadOfThrowing()
    {
        // Defence in depth: the predicate keeps such a tree away from the emitter, but a null can also
        // sit DEEPER than the root, where no usability check looks. Emitting must not NullReference.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("orderNumber", Canon("PoNumber")),
                new OutputNode
                {
                    Name = "nested",
                    NodeType = OutputNodeType.Object,
                    Children = new List<OutputNode> { null! },
                }),
        };

        var result = new OutputTemplateEmitter().Emit(tree, ResolvedOrder(), new OrderMappingOverride());

        using var reader = new StreamReader(result.Content);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal("PO-1", doc.RootElement.GetProperty("orderNumber").GetString());
    }

    // ══ D4 — envelope identity is only identity for the format that reads it ═════════════════════

    private static EnvelopeConfig CxmlOnly() => new()
    {
        Cxml = new CxmlEnvelope { FromDomain = "NetworkId", FromIdentity = "ACME" },
    };

    private static EnvelopeConfig X12Only() => new()
    {
        X12 = new X12Envelope { IsaSenderQualifier = "01", IsaSenderId = "ACME-BUYER" },
    };

    [Fact]
    public void ACxmlTreeCarryingOnlyAnX12Envelope_IsNotUsable()
    {
        // CxmlTransformService reads envelope.Cxml and nothing else, so an X12-only envelope on a cXML
        // template changes not one delivered byte. Calling it "usable" gave byte-identical artifacts
        // different provenance digests.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.CXml,
            Root = DrawnRoot(),
            Envelope = X12Only(),
        };

        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(tree));
    }

    [Fact]
    public void AnX12TreeCarryingOnlyACxmlEnvelope_IsNotUsable()
    {
        // The mirror case. X12TransformService reads `envelope?.X12` — a cXML-only envelope is null to it.
        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.X12,
            Root = DrawnRoot(),
            Envelope = CxmlOnly(),
        };

        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(tree));
    }

    [Fact]
    public void MatchingEnvelopeIdentityStaysUsable()
    {
        // Assert the DIFFERENCE: an envelope-only tree DOES have a job when the format reads it.
        Assert.True(OrderMappingOverrideReader.HasUsableOutputTree(new OutputNodeTemplate
        {
            Format = OutputFormat.CXml, Root = DrawnRoot(), Envelope = CxmlOnly(),
        }));
        Assert.True(OrderMappingOverrideReader.HasUsableOutputTree(new OutputNodeTemplate
        {
            Format = OutputFormat.X12, Root = DrawnRoot(), Envelope = X12Only(),
        }));
    }

    [Theory]
    [InlineData(OutputFormat.Ubl)]
    [InlineData(OutputFormat.UblOrder)]
    [InlineData(OutputFormat.X12_850)]
    [InlineData(OutputFormat.EdifactOrders)]
    public void NoTransformReadsAnEnvelopeForTheseFormats_SoAnEnvelopeCannotMakeThemUsable(OutputFormat format)
    {
        // Only the cXML and X12 transforms have an envelope overload. Admitting a Ubl/EDIFACT tree
        // because it "has an envelope" would re-open P0-1 through the envelope door.
        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(new OutputNodeTemplate
        {
            Format = format, Root = DrawnRoot(), Envelope = CxmlOnly(),
        }));
        Assert.False(OrderMappingOverrideReader.HasUsableOutputTree(new OutputNodeTemplate
        {
            Format = format, Root = DrawnRoot(), Envelope = X12Only(),
        }));
    }
}
