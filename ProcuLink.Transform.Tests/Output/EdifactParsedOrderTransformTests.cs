using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class EdifactParsedOrderTransformTests
{
    private static ParsedOrder SampleOrder(IReadOnlyList<ParsedOrderLine>? lines = null) =>
        new(
            PoNumber:  "PO-EDI-001",
            OrderDate: new DateTime(2026, 5, 30),
            BuyerName: "Acme Buyer Ltd",
            Currency:  "EUR",
            Lines: lines ?? new List<ParsedOrderLine>
            {
                new(LineNumber: 1, BuyerItemCode: "BUYER-001", Description: "Widget Type A", Quantity: 10m, Unit: "EA", UnitPrice: 125.00m),
            });

    /// <summary>Split on the segment terminator (no release handling needed for tag inspection).</summary>
    private static List<string> SplitSegments(string edi) =>
        edi.Split('\'', StringSplitOptions.RemoveEmptyEntries)
           .Select(s => s.Trim('\r', '\n', ' ', '\t'))
           .Where(s => s.Length > 0)
           .ToList();

    [Fact]
    public void CanTransform_ReturnsTrueForEdifactOrdersOnly()
    {
        var svc = new EdifactParsedOrderTransform();
        svc.CanTransform(OutputFormat.EdifactOrders).Should().BeTrue();
        svc.CanTransform(OutputFormat.UblOrder).Should().BeFalse();
        svc.CanTransform(OutputFormat.X12_850).Should().BeFalse();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
    }

    [Fact]
    public void Transform_EmitsValidOrdersEnvelope()
    {
        var svc    = new EdifactParsedOrderTransform();
        var result = svc.Transform(SampleOrder(), OutputFormat.EdifactOrders);

        result.ContentType.Should().Be("application/edifact");
        result.FileExtension.Should().Be(".edi");

        var segs = SplitSegments(result.AsText());
        var tags = segs.Select(s => s.Split('+')[0]).ToList();

        // Required envelope tags present and ordered.
        tags.Should().ContainInOrder("UNB", "UNH", "BGM", "DTM", "NAD", "CUX", "LIN", "QTY", "UNS", "UNT", "UNZ");

        segs.Should().Contain("UNH+1+ORDERS:D:96A:UN");
        segs.Should().Contain("BGM+220+PO-EDI-001+9");
        segs.Should().Contain("DTM+137:20260530:102");
        segs.Should().Contain("NAD+BY+++Acme Buyer Ltd");
        segs.Should().Contain("CUX+2:EUR:9");
        segs.Should().Contain("LIN+1++BUYER-001:IN");
        segs.Should().Contain("QTY+21:10:EA");
        segs.Should().Contain("PRI+AAA:125.00");
        segs.Should().Contain("UNS+S");
        segs[^1].Should().Be("UNZ+1+1");
    }

    [Fact]
    public void Transform_UntCount_CoversUnhToUntInclusive()
    {
        var svc  = new EdifactParsedOrderTransform();
        var segs = SplitSegments(svc.Transform(SampleOrder(), OutputFormat.EdifactOrders).AsText());

        // Count segments from UNH up to and including UNT.
        var unhIndex = segs.FindIndex(s => s.StartsWith("UNH"));
        var untIndex = segs.FindIndex(s => s.StartsWith("UNT"));
        var actualCount = untIndex - unhIndex + 1;

        var unt = segs.Single(s => s.StartsWith("UNT+"));
        var declaredCount = int.Parse(unt.Split('+')[1]);

        declaredCount.Should().Be(actualCount);
    }

    [Fact]
    public async Task Transform_RoundTripsThroughEdifactOrderParser()
    {
        var svc    = new EdifactParsedOrderTransform();
        var source = SampleOrder(new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "BUYER-RT-1", Description: "Round-trip widget", Quantity: 4m,   Unit: "EA", UnitPrice: 50.00m),
            new(LineNumber: 2, BuyerItemCode: "BUYER-RT-2", Description: "Round-trip bolt",   Quantity: 250m, Unit: "PCE", UnitPrice: 0.45m),
        });

        var result = svc.Transform(source, OutputFormat.EdifactOrders);

        using var stream = new MemoryStream(result.Content);
        var parsed = await new EdifactOrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-EDI-001");
        parsed.OrderDate.Should().Be(new DateTime(2026, 5, 30));
        parsed.Currency.Should().Be("EUR");
        parsed.BuyerName.Should().Be("Acme Buyer Ltd");
        parsed.Lines.Should().HaveCount(2);

        var first = parsed.Lines.OrderBy(l => l.LineNumber).First();
        first.LineNumber.Should().Be(1);
        first.BuyerItemCode.Should().Be("BUYER-RT-1");
        first.Description.Should().Be("Round-trip widget");
        first.Quantity.Should().Be(4m);
        first.Unit.Should().Be("EA");
        first.UnitPrice.Should().Be(50.00m);

        var second = parsed.Lines.OrderBy(l => l.LineNumber).Last();
        second.BuyerItemCode.Should().Be("BUYER-RT-2");
        second.Quantity.Should().Be(250m);
        second.Unit.Should().Be("PCE");
        second.UnitPrice.Should().Be(0.45m);
    }

    [Fact]
    public void Transform_ReleaseEscapesDelimiterCharactersInFreeText()
    {
        // A description containing the EDIFACT release char '?', a component
        // separator ':', and an element separator '+' must be release-escaped on
        // write (each special char prefixed with '?') so it cannot corrupt the
        // segment structure. This asserts the transform-side ISO 9735 contract.
        var svc    = new EdifactParsedOrderTransform();
        var source = SampleOrder(new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "ITEM-1", Description: "Cable 2:1 ratio? a+b", Quantity: 1m, Unit: "EA", UnitPrice: 9.99m),
        });

        var edi = svc.Transform(source, OutputFormat.EdifactOrders).AsText();

        // ':' -> '?:'   '?' -> '??'   '+' -> '?+'
        edi.Should().Contain("Cable 2?:1 ratio?? a?+b");

        // The escaped specials must not increase the segment / element / component
        // count: there is still exactly one IMD segment and one UNH..UNT message.
        SplitSegments(edi).Count(s => s.StartsWith("IMD")).Should().Be(1);
        SplitSegments(edi).Count(s => s.StartsWith("UNH")).Should().Be(1);
        SplitSegments(edi).Count(s => s.StartsWith("UNT")).Should().Be(1);
    }
}
