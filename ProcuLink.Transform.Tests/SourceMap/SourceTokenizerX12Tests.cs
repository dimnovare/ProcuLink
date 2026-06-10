using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// Tests for <see cref="SourceTokenizer"/> ANSI X12 850 tokenisation, focused on the
/// improved human-readable labels (segment + element meaning). The token id (the stable
/// address <c>seg:{TAG}[{n}].el{element}</c>) is unchanged; only the label is asserted here.
/// </summary>
public class SourceTokenizerX12Tests
{
    private static readonly SourceTokenizer Tokenizer = new();

    // Minimal X12 850 using the canonical default delimiters: element '*', segment '~'.
    // BEG*00*NE*PO-X12-001**20240115 → el3 = PO number
    // PO1*1*10*EA*5.50**BP*WIDGET-A   → el2 = quantity, el4 = unit price, el7 = item code
    private const string MinimalPurchaseOrder =
        "ST*850*0001~" +
        "BEG*00*NE*PO-X12-001**20240115~" +
        "REF*DP*DEPT-7~" +
        "N1*BY*Acme Buyer Ltd~" +
        "PO1*1*10*EA*5.50**BP*WIDGET-A~" +
        "PID*F****Widget Type A~" +
        "PO1*2*5*EA*9.99**BP*GADGET-B~" +
        "CTT*2~" +
        "SE*8*0001~";

    [Fact]
    public async Task X12_BegPoNumber_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalPurchaseOrder);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        var token = tokens.First(t => t.Id == "seg:BEG[1].el3");
        token.Value.Should().Be("PO-X12-001");
        token.Label.Should().Be("BEG PO number");
    }

    [Fact]
    public async Task X12_Po1Quantity_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalPurchaseOrder);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        var token = tokens.First(t => t.Id == "seg:PO1[1].el2");
        token.Value.Should().Be("10");
        token.Label.Should().Be("PO1 quantity");
    }

    [Fact]
    public async Task X12_Po1ItemCode_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalPurchaseOrder);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        var token = tokens.First(t => t.Id == "seg:PO1[1].el7");
        token.Value.Should().Be("WIDGET-A");
        token.Label.Should().Be("PO1 item code");
    }

    [Fact]
    public async Task X12_RepeatedTag_LabelCarriesOccurrenceSuffix()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalPurchaseOrder);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        var first  = tokens.First(t => t.Id == "seg:PO1[1].el4");
        first.Label.Should().Be("PO1 unit price");
        var second = tokens.First(t => t.Id == "seg:PO1[2].el4");
        second.Label.Should().Be("PO1 unit price #2");
    }

    [Fact]
    public async Task X12_UnknownPosition_FallsBackToElementNumber()
    {
        // A segment with no entry in the meaning table falls back to "{TAG} element {n}".
        const string msg = "ZZZ*foo*bar~";
        var bytes = Encoding.UTF8.GetBytes(msg);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        var token = tokens.First(t => t.Id == "seg:ZZZ[1].el1");
        token.Label.Should().Be("ZZZ element 1");
    }

    [Fact]
    public async Task X12_HeaderVsLineGrouping_StillCorrect()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalPurchaseOrder);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".x12");

        // BEG/REF/N1 are before the first PO1 → header; PO1/PID are line.
        tokens.Where(t => t.Id.StartsWith("seg:BEG")).Should().OnlyContain(t => t.Group == "header");
        tokens.Where(t => t.Id.StartsWith("seg:PO1")).Should().OnlyContain(t => t.Group == "line");
    }
}
