using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class X12ParsedOrderTransformTests
{
    private static ParsedOrder SampleOrder(IReadOnlyList<ParsedOrderLine>? lines = null) =>
        new(
            PoNumber:  "PO-X12-001",
            OrderDate: new DateTime(2026, 5, 30),
            BuyerName: "Acme Buyer Ltd",
            Currency:  "USD",
            Lines: lines ?? new List<ParsedOrderLine>
            {
                new(LineNumber: 1, BuyerItemCode: "BUYER-001", Description: "Widget Type A", Quantity: 10m, Unit: "EA", UnitPrice: 125.00m),
            });

    private static List<string> SplitSegments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries)
           .Select(s => s.Trim('\r', '\n', ' ', '\t'))
           .Where(s => s.Length > 0)
           .ToList();

    [Fact]
    public void CanTransform_ReturnsTrueForX12_850Only()
    {
        var svc = new X12ParsedOrderTransform();
        svc.CanTransform(OutputFormat.X12_850).Should().BeTrue();
        svc.CanTransform(OutputFormat.UblOrder).Should().BeFalse();
        svc.CanTransform(OutputFormat.EdifactOrders).Should().BeFalse();
        svc.CanTransform(OutputFormat.X12).Should().BeFalse(); // entity-based sibling value
    }

    [Fact]
    public void Transform_EmitsWellFormed850Envelope()
    {
        var svc    = new X12ParsedOrderTransform();
        var result = svc.Transform(SampleOrder(), OutputFormat.X12_850);

        result.ContentType.Should().Be("application/edi-x12");
        result.FileExtension.Should().Be(".x12");

        var edi  = result.AsText();
        var segs = SplitSegments(edi);

        segs[0].Should().StartWith("ISA*");
        segs[1].Should().StartWith("GS*PO*");
        segs[2].Should().Be("ST*850*0001");
        segs[3].Should().StartWith("BEG*00*NE*PO-X12-001**20260530");
        segs.Should().Contain(s => s.StartsWith("CUR*BY*USD"));
        segs.Should().Contain("N1*BY*Acme Buyer Ltd");
        segs.Should().Contain(s => s.StartsWith("PO1*1*10*EA*125.00*PE*BP*BUYER-001"));
        segs.Should().Contain("CTT*1");
        segs[^2].Should().Be("GE*1*1");
        segs[^1].Should().Be("IEA*1*000000001");

        // Fixed-width ISA: element separator after "ISA", component separator at
        // ISA16 (index 104), segment terminator at index 105.
        edi[3].Should().Be('*');
        edi[104].Should().Be('>');
        edi[105].Should().Be('~');
    }

    [Fact]
    public void Transform_ComputesBalancedSeAndCttCounts()
    {
        var lines = new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "B1", Description: "First",  Quantity: 1m, Unit: "EA", UnitPrice: 1m),
            new(LineNumber: 2, BuyerItemCode: "B2", Description: "Second", Quantity: 2m, Unit: "EA", UnitPrice: 2m),
        };

        var svc  = new X12ParsedOrderTransform();
        var segs = SplitSegments(svc.Transform(SampleOrder(lines), OutputFormat.X12_850).AsText());

        // CTT = number of PO1 line items.
        segs.Should().Contain("CTT*2");

        // SE count = ST … SE inclusive.
        //   ST, BEG, CUR, N1, PO1, PID, PO1, PID, CTT, SE → 10
        segs.Single(s => s.StartsWith("SE*")).Should().Be("SE*10*0001");
    }

    [Fact]
    public async Task Transform_RoundTripsThroughX12OrderParser()
    {
        var svc    = new X12ParsedOrderTransform();
        var source = SampleOrder(new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "BUYER-RT-1", Description: "Round-trip widget", Quantity: 4m,   Unit: "EA", UnitPrice: 50.00m),
            new(LineNumber: 2, BuyerItemCode: "BUYER-RT-2", Description: "Round-trip bolt",   Quantity: 250m, Unit: "EA", UnitPrice: 0.45m),
        });

        var result = svc.Transform(source, OutputFormat.X12_850);

        using var stream = new MemoryStream(result.Content);
        var parsed = await new X12OrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-X12-001");
        parsed.OrderDate.Should().Be(new DateTime(2026, 5, 30));
        parsed.Currency.Should().Be("USD");
        parsed.BuyerName.Should().Be("Acme Buyer Ltd");
        parsed.Lines.Should().HaveCount(2);

        var first = parsed.Lines.OrderBy(l => l.LineNumber).First();
        first.BuyerItemCode.Should().Be("BUYER-RT-1"); // recovered from BP qualifier
        first.Quantity.Should().Be(4m);
        first.UnitPrice.Should().Be(50.00m);
        first.Unit.Should().Be("EA");
        first.Description.Should().Be("Round-trip widget");

        var second = parsed.Lines.OrderBy(l => l.LineNumber).Last();
        second.BuyerItemCode.Should().Be("BUYER-RT-2");
        second.Quantity.Should().Be(250m);
        second.UnitPrice.Should().Be(0.45m);
    }

    [Fact]
    public void Transform_SanitizesDelimiterCharactersInFreeText()
    {
        var lines = new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "B*1>x", Description: "Has*star>and~tilde", Quantity: 1m, Unit: "EA", UnitPrice: 1m),
        };

        var svc = new X12ParsedOrderTransform();
        var edi = svc.Transform(SampleOrder(lines), OutputFormat.X12_850).AsText();

        // Exactly one ST and one SE survive — free-text delimiters did not inject segments.
        SplitSegments(edi).Count(s => s.StartsWith("ST*")).Should().Be(1);
        SplitSegments(edi).Count(s => s.StartsWith("SE*")).Should().Be(1);
        // The raw '~' from the description must not appear unescaped inside a PID value.
        edi.Should().Contain("PID*F****Has star and tilde");
    }
}
