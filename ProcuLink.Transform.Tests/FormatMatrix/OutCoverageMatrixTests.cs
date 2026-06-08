using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// OUT-coverage leg of the FormatMatrix stress suite. Every output transform is
/// exercised against a canonical order and asserted to produce a WELL-FORMED
/// document (valid XML / parseable CSV / valid JSON) whose key fields round-trip.
///
/// Two transform families are covered:
///   • IParsedOrderTransform (consumes ParsedOrder directly, via ParsedOrderTransformFactory):
///       UblOrder, X12_850, EdifactOrders.
///   • ITransformService (consumes a resolved PurchaseOrderEntity):
///       Csv, Json, Xml, CXml, Ubl, X12  — plus MappedTransformService (CSV/JSON overrides).
/// </summary>
public class OutCoverageMatrixTests
{
    private static ParsedOrder CanonicalOrder() => new(
        PoNumber:  "PO-OUT-1",
        OrderDate: new DateTime(2026, 6, 8),
        BuyerName: "Acme Buyer Ltd",
        Currency:  "EUR",
        Lines: new List<ParsedOrderLine>
        {
            new(1, "BUY-001", "Widget Type A", 10m, "EA", 12.50m),
            new(2, "BUY-002", "Widget Type B", 5m,  "EA", 8.00m),
        });

    // A resolved entity for the ITransformService family (needs a non-null
    // SupplierItemCode + NeedsReview=false on every line, else the validation guard throws).
    private static PurchaseOrderEntity ResolvedEntity()
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-OUT-1",
            BuyerName  = "Acme Buyer Ltd",
            OrderDate  = new DateOnly(2026, 6, 8),
            Currency   = "EUR",
            Status     = "ready",
            Supplier   = new Supplier { Name = "Matrix Supplier OY" },
            Lines = new List<PurchaseOrderLineEntity>
            {
                new() { LineNumber = 1, BuyerItemCode = "BUY-001", SupplierItemCode = "SUP-1",
                        Description = "Widget Type A", Quantity = 10m, Unit = "EA", UnitPrice = 12.50m,
                        NeedsReview = false, Confidence = 1.0f },
                new() { LineNumber = 2, BuyerItemCode = "BUY-002", SupplierItemCode = "SUP-2",
                        Description = "Widget Type B", Quantity = 5m, Unit = "EA", UnitPrice = 8.00m,
                        NeedsReview = false, Confidence = 1.0f },
            },
        };
        return order;
    }

    private static ParsedOrderTransformFactory ParsedFactory() =>
        new(new IParsedOrderTransform[]
        {
            new UblParsedOrderTransform(),
            new X12ParsedOrderTransform(),
            new EdifactParsedOrderTransform(),
        });

    private static string ReadStream(TransformResult r)
    {
        r.Content.Position = 0;
        // leaveOpen so callers can read the same result more than once.
        using var sr = new StreamReader(r.Content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return sr.ReadToEnd();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // IParsedOrderTransform family — well-formed + round-trip
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Out_UblOrder_IsWellFormedXml_AndCarriesKeyFields()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.UblOrder);
        r.ContentType.Should().Be("application/xml");

        var doc = XDocument.Parse(r.AsText()); // throws if not well-formed
        doc.Root!.Name.LocalName.Should().Be("Order");
        r.AsText().Should().Contain("PO-OUT-1").And.Contain("BUY-001").And.Contain("EUR");
    }

    [Fact]
    public async Task Out_UblOrder_RoundTripsThroughUblOrderParser()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.UblOrder);
        using var stream = new MemoryStream(r.Content);
        var parsed = await new UblOrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-OUT-1");
        parsed.Currency.Should().Be("EUR");
        parsed.Lines.Should().HaveCount(2);
        parsed.Lines.OrderBy(l => l.LineNumber).First().BuyerItemCode.Should().Be("BUY-001");
        parsed.Lines.OrderBy(l => l.LineNumber).First().UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public void Out_X12_850_IsWellFormedEnvelope_AndCarriesKeyFields()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.X12_850);
        r.ContentType.Should().Be("application/edi-x12");

        var segs = r.AsText().Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('\r', '\n', ' ', '\t')).Where(s => s.Length > 0).ToList();
        segs[0].Should().StartWith("ISA*");
        segs.Should().Contain(s => s.StartsWith("ST*850"));
        segs.Should().Contain(s => s.StartsWith("BEG*00*NE*PO-OUT-1"));
    }

    [Fact]
    public async Task Out_X12_850_RoundTripsThroughX12OrderParser()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.X12_850);
        using var stream = new MemoryStream(r.Content);
        var parsed = await new X12OrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-OUT-1");
        parsed.Currency.Should().Be("EUR");
        parsed.Lines.Should().HaveCount(2);
        parsed.Lines.OrderBy(l => l.LineNumber).First().BuyerItemCode.Should().Be("BUY-001");
    }

    [Fact]
    public void Out_EdifactOrders_IsWellFormed_AndCarriesKeyFields()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.EdifactOrders);
        r.ContentType.Should().Be("application/edifact");

        var text = r.AsText();
        text.Should().StartWith("UNB+");
        text.Should().Contain("UNH+").And.Contain("ORDERS:D:96A:UN");
        text.Should().Contain("BGM+220+PO-OUT-1");
    }

    [Fact]
    public async Task Out_EdifactOrders_RoundTripsThroughEdifactOrderParser()
    {
        var r = ParsedFactory().Transform(CanonicalOrder(), OutputFormat.EdifactOrders);
        using var stream = new MemoryStream(r.Content);
        var parsed = await new EdifactOrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-OUT-1");
        parsed.Currency.Should().Be("EUR");
        parsed.Lines.Should().HaveCount(2);
        parsed.Lines.OrderBy(l => l.LineNumber).First().BuyerItemCode.Should().Be("BUY-001");
        parsed.Lines.OrderBy(l => l.LineNumber).First().UnitPrice.Should().Be(12.50m);
    }

    [Fact]
    public void Out_ParsedFactory_UnknownFormat_Throws()
    {
        // The ITransformService values (Csv/Json/Xml/CXml/Ubl/X12) are NOT registered
        // in the ParsedOrder factory — asking for one is a clear NotSupportedException.
        var act = () => ParsedFactory().Transform(CanonicalOrder(), OutputFormat.Csv);
        act.Should().Throw<NotSupportedException>();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // ITransformService family (entity-based) — well-formed + key fields
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Out_EntityCsv_IsParseableCsv_AndCarriesResolvedCodes()
    {
        var r = await new CsvTransformService().TransformAsync(ResolvedEntity(), OutputFormat.Csv, CancellationToken.None);
        var text = ReadStream(r);

        var rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        rows[0].Should().StartWith("SupplierItemCode,");
        rows.Should().HaveCount(3); // header + 2 lines
        rows[1].Should().StartWith("SUP-1,");
    }

    [Fact]
    public async Task Out_EntityJson_IsValidJson_AndCarriesKeyFields()
    {
        var r = await new JsonTransformService().TransformAsync(ResolvedEntity(), OutputFormat.Json, CancellationToken.None);
        r.Content.Position = 0;
        using var doc = JsonDocument.Parse(r.Content); // throws if invalid

        doc.RootElement.GetProperty("poNumber").GetString().Should().Be("PO-OUT-1");
        doc.RootElement.GetProperty("currency").GetString().Should().Be("EUR");
        doc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Out_EntityXml_IsWellFormedXml_AndCarriesKeyFields()
    {
        var r = await new XmlTransformService().TransformAsync(ResolvedEntity(), OutputFormat.Xml, CancellationToken.None);
        var doc = XDocument.Parse(ReadStream(r));

        doc.Root!.Name.LocalName.Should().Be("PurchaseOrder");
        doc.Descendants("PoNumber").Single().Value.Should().Be("PO-OUT-1");
        doc.Descendants("Line").Should().HaveCount(2);
    }

    [Fact]
    public async Task Out_EntityCxml_IsWellFormedXml_AndCarriesKeyFields()
    {
        var r = await new CxmlTransformService().TransformAsync(ResolvedEntity(), OutputFormat.CXml, CancellationToken.None);
        var doc = XDocument.Parse(ReadStream(r));

        doc.Root!.Name.LocalName.Should().Be("cXML");
        ReadStream(r).Should().Contain("PO-OUT-1");
    }

    [Fact]
    public async Task Out_EntityUbl_IsWellFormedXml()
    {
        var r = await new UblOrderTransformService().TransformAsync(ResolvedEntity(), OutputFormat.Ubl, CancellationToken.None);
        var doc = XDocument.Parse(ReadStream(r));
        doc.Root!.Name.LocalName.Should().Be("Order");
        ReadStream(r).Should().Contain("PO-OUT-1");
    }

    [Fact]
    public async Task Out_EntityX12_IsWellFormedEnvelope()
    {
        var r = await new X12TransformService().TransformAsync(ResolvedEntity(), OutputFormat.X12, CancellationToken.None);
        var text = ReadStream(r);
        text.Should().StartWith("ISA*");
        text.Should().Contain("ST*850");
    }

    [Fact]
    public async Task Out_EntityTransforms_ThrowOnUnresolvedLines()
    {
        // The validation guard: a line with NeedsReview=true must never serialize —
        // every entity transform throws TransformValidationException.
        var order = ResolvedEntity();
        order.Lines[1].NeedsReview = true;

        var csv  = async () => await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        var json = async () => await new JsonTransformService().TransformAsync(order, OutputFormat.Json, CancellationToken.None);
        var xml  = async () => await new XmlTransformService().TransformAsync(order, OutputFormat.Xml, CancellationToken.None);

        await csv.Should().ThrowAsync<TransformValidationException>();
        await json.Should().ThrowAsync<TransformValidationException>();
        await xml.Should().ThrowAsync<TransformValidationException>();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // MappedTransformService — override-driven CSV + JSON
    // ════════════════════════════════════════════════════════════════════════════

    private static OrderMappingOverride SampleOverride() => new()
    {
        Output = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
            Lines  =
            {
                ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" },
                ["qty"]  = new OutputFieldRule { OutputPath = "Qty",      CanonicalField = "Quantity" },
            },
        },
    };

    [Fact]
    public void Out_MappedCsv_UsesOverridePaths_AndIsParseable()
    {
        var r = new MappedTransformService().Build(ResolvedEntity(), SampleOverride(), OutputFormat.Csv);
        var text = ReadStream(r);

        var rows = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        rows[0].Should().Be("OrderRef,ItemCode,Qty");      // override output paths
        rows[1].Should().StartWith("PO-OUT-1,SUP-1,10");
    }

    [Fact]
    public void Out_MappedJson_UsesOverridePaths_AndIsValidJson()
    {
        var r = new MappedTransformService().Build(ResolvedEntity(), SampleOverride(), OutputFormat.Json);
        r.Content.Position = 0;
        using var doc = JsonDocument.Parse(r.Content);

        doc.RootElement.GetProperty("header").GetProperty("OrderRef").GetString().Should().Be("PO-OUT-1");
        var lines = doc.RootElement.GetProperty("lines");
        lines.GetArrayLength().Should().Be(2);
        lines[0].GetProperty("ItemCode").GetString().Should().Be("SUP-1");
    }

    [Fact]
    public void Out_MappedTransform_RejectsNonFlatFormat()
    {
        var act = () => new MappedTransformService().Build(ResolvedEntity(), SampleOverride(), OutputFormat.Xml);
        act.Should().Throw<ArgumentException>();
    }
}
