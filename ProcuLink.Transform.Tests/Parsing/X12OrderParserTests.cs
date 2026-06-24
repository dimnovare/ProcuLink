using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class X12OrderParserTests
{
    // X12 850 uses '*' element / '~' segment / '>' component by convention. A CRLF
    // after each '~' is for readability only — the parser strips control whitespace.
    private const string NL = "\r\n";

    private static Stream ToStream(string edi) =>
        new MemoryStream(Encoding.UTF8.GetBytes(edi));

    /// <summary>
    /// Builds a conformant fixed-width ISA segment (exactly 105 chars before the
    /// '~' terminator) so positional delimiter discovery resolves correctly.
    /// </summary>
    private static string Isa() =>
        "ISA" +
        "*00" +
        "*" + new string(' ', 10) +
        "*00" +
        "*" + new string(' ', 10) +
        "*ZZ" +
        "*" + "SENDER".PadRight(15) +
        "*ZZ" +
        "*" + "RECEIVER".PadRight(15) +
        "*260529" +
        "*0830" +
        "*U" +
        "*00401" +
        "*000000001" +
        "*0" +
        "*P" +
        "*>" +
        "~" + NL;

    private static string Header(string poNumber = "PO-12345", string date = "20260529", string currency = "USD") =>
        Isa() +
        "GS*PO*SENDER*RECEIVER*20260529*0830*1*X*004010~" + NL +
        "ST*850*0001~" + NL +
        $"BEG*00*NE*{poNumber}**{date}~" + NL +
        $"CUR*BY*{currency}~" + NL +
        "N1*BY*Acme Buying Corp~" + NL +
        "N1*ST*Acme Receiving Dock 4~" + NL;

    private static string Trailer(int lineCount = 1, int segCount = 6) =>
        $"CTT*{lineCount}~" + NL +
        $"SE*{segCount}*0001~" + NL +
        "GE*1*1~" + NL +
        "IEA*1*000000001~" + NL;

    // ── CanParse / detection ────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrueForX12Only()
    {
        var parser = new X12OrderParser();
        parser.CanParse(".x12").Should().BeTrue();
        parser.CanParse(".X12").Should().BeTrue();
        parser.CanParse(".edi").Should().BeFalse();  // shared with EDIFACT — factory disambiguates by content
        parser.CanParse(".txt").Should().BeFalse();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".xml").Should().BeFalse();
    }

    [Fact]
    public void IsX12OrderDocument_RecognisesIsaWith850()
    {
        X12OrderParser.IsX12OrderDocument("ISA*00*   *ST*850*0001~").Should().BeTrue();
        X12OrderParser.IsX12OrderDocument("  \r\nISA*00* ... ST*850").Should().BeTrue();
        X12OrderParser.IsX12OrderDocument("ISA*00* ... ST*810*0001").Should().BeFalse(); // 810 invoice, not 850
        X12OrderParser.IsX12OrderDocument("UNB+UNOC:3+...").Should().BeFalse();           // EDIFACT
        X12OrderParser.IsX12OrderDocument("<?xml version=\"1.0\"?>").Should().BeFalse();
        X12OrderParser.IsX12OrderDocument("").Should().BeFalse();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_GoldenOrderWithThreeLines_MapsAllFields()
    {
        var edi =
            Header(poNumber: "PO-HAPPY-1", date: "20260529", currency: "USD") +
            "PO1*1*12*EA*4.50*PE*BP*ACME-WIDGET-A*VP*SUP-001~" + NL +
            "PID*F****Widget A 10mm~" + NL +
            "PO1*2*250*EA*0.45*PE*BP*ACME-BOLT-B*VP*SUP-002~" + NL +
            "PID*F****Bolt M8x40~" + NL +
            "PO1*3*5*EA*89.00*PE*BP*ACME-SHEET-C~" + NL +
            Trailer(lineCount: 3, segCount: 10);

        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.PoNumber.Should().Be("PO-HAPPY-1");
        result.OrderDate.Should().Be(new DateTime(2026, 5, 29));
        result.Currency.Should().Be("USD");
        result.BuyerName.Should().Be("Acme Buying Corp");
        result.Lines.Should().HaveCount(3);

        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("ACME-WIDGET-A");
        result.Lines[0].Description.Should().Be("Widget A 10mm");
        result.Lines[0].Quantity.Should().Be(12m);
        result.Lines[0].Unit.Should().Be("EA");
        result.Lines[0].UnitPrice.Should().Be(4.50m);

        result.Lines[1].BuyerItemCode.Should().Be("ACME-BOLT-B");
        result.Lines[1].Quantity.Should().Be(250m);
        result.Lines[1].UnitPrice.Should().Be(0.45m);

        // Third line has no PID and no VP — buyer part (BP) only.
        result.Lines[2].BuyerItemCode.Should().Be("ACME-SHEET-C");
        result.Lines[2].Quantity.Should().Be(5m);
        result.Lines[2].Description.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_BuyerCodeFallsBackToVendorPart_WhenNoBuyerQualifier()
    {
        // Only a VP (vendor part) qualifier present — used as the buyer item code fallback.
        var edi =
            Header() +
            "PO1*1*10*EA*5.00*PE*VP*VENDOR-ONLY-9~" + NL +
            Trailer(lineCount: 1, segCount: 6);

        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("VENDOR-ONLY-9");
    }

    [Fact]
    public async Task ParseAsync_DefaultsCurrencyToUsd_WhenNoCurSegment()
    {
        var edi =
            Isa() +
            "GS*PO*SENDER*RECEIVER*20260529*0830*1*X*004010~" + NL +
            "ST*850*0001~" + NL +
            "BEG*00*NE*PO-NOCUR**20260529~" + NL +
            "PO1*1*1*EA*1.00*PE*BP*X~" + NL +
            Trailer(lineCount: 1, segCount: 5);

        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        // No CUR segment — parser leaves Currency null; the canonical default is
        // applied downstream / by the transformer, not the parser.
        result.Currency.Should().BeNull();
        result.PoNumber.Should().Be("PO-NOCUR");
    }

    // ── Resilience ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_UnknownAndMalformedOptionalSegments_AreSkipped()
    {
        // REF/DTM/TD5 and a totally unknown ZZZ segment must not break parsing.
        var edi =
            Header() +
            "REF*DP*DEPT-42~" + NL +
            "DTM*002*20260605~" + NL +
            "ZZZ*this*is*garbage*we*do*not*understand~" + NL +
            "TD5~" + NL +                       // truncated optional segment, no elements
            "PO1*1*7*EA*3.50*PE*BP*ITEM-OK~" + NL +
            "ZZZ*more*junk~" + NL +
            Trailer(lineCount: 1, segCount: 6);

        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.PoNumber.Should().Be("PO-12345");
        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("ITEM-OK");
        result.Lines[0].Quantity.Should().Be(7m);
        result.Lines[0].UnitPrice.Should().Be(3.50m);
    }

    [Fact]
    public async Task ParseAsync_NonConformantShortIsa_FallsBackToDefaultDelimiters()
    {
        // A short / hand-built ISA (not fixed-width) must not corrupt tokenization;
        // the element separator is still recovered from index 3 and the rest fall
        // back to the canonical defaults.
        var edi =
            "ISA*00*SENDER*00*RECEIVER*ZZ*S*ZZ*R*260529*0830*U*00401*1*0*P*>~" + NL +
            "ST*850*0001~" + NL +
            "BEG*00*NE*PO-SHORT**20260529~" + NL +
            "PO1*1*2*EA*9.99*PE*BP*SHORT-1~" + NL +
            "SE*4*0001~" + NL;

        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.PoNumber.Should().Be("PO-SHORT");
        result.Lines.Should().ContainSingle();
        result.Lines[0].BuyerItemCode.Should().Be("SHORT-1");
        result.Lines[0].UnitPrice.Should().Be(9.99m);
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_EmptyInput_ThrowsX12ParseException()
    {
        var parser = new X12OrderParser();
        var act = async () => await parser.ParseAsync(ToStream(""), CancellationToken.None);
        await act.Should().ThrowAsync<X12ParseException>().WithMessage("*empty*");
    }

    [Fact]
    public async Task ParseAsync_MissingIsa_ThrowsX12ParseException()
    {
        var edi = "ST*850*0001~" + NL + "BEG*00*NE*PO-1**20260529~" + NL;
        var parser = new X12OrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);
        await act.Should().ThrowAsync<X12ParseException>().WithMessage("*ISA*");
    }

    [Fact]
    public async Task ParseAsync_WrongTransactionSet_ThrowsX12ParseException()
    {
        // 810 invoice, not 850 purchase order.
        var edi =
            Isa() +
            "GS*IN*SENDER*RECEIVER*20260529*0830*1*X*004010~" + NL +
            "ST*810*0001~" + NL +
            "BIG*20260529*INV-1~" + NL +
            "SE*3*0001~" + NL;

        var parser = new X12OrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);
        await act.Should().ThrowAsync<X12ParseException>().WithMessage("*850*");
    }

    [Fact]
    public async Task ParseAsync_MissingBeg_ThrowsX12ParseException()
    {
        var edi =
            Isa() +
            "GS*PO*SENDER*RECEIVER*20260529*0830*1*X*004010~" + NL +
            "ST*850*0001~" + NL +
            "PO1*1*1*EA*1.00*PE*BP*X~" + NL +
            "SE*3*0001~" + NL;

        var parser = new X12OrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);
        await act.Should().ThrowAsync<X12ParseException>().WithMessage("*BEG*");
    }

    // ── B3: locale-aware decimal parsing (shared NumberParsing reader) ───────────
    // X12 is an invariant-locale standard ('.' decimal, ',' groups thousands). The old
    // naive ParseDecimal only swapped ','→'.' when no '.' was present, so EU "1.234,56"
    // collapsed to 1.23456 (group dropped) and "73,22" passed through as the catastrophic
    // 7322. Both are now read correctly OR flagged NeedsReview — never silently wrong.

    private static async Task<ParsedOrderLine> ParsePo1PriceAsync(string priceElement)
    {
        // '*' is the X12 element separator, so a ',' inside the price is a normal character,
        // not a delimiter — the price element survives intact.
        var edi =
            Header() +
            $"PO1*1*2*EA*{priceElement}*PE*BP*ITEM-1~" + NL +
            Trailer(lineCount: 1, segCount: 6);
        var parser = new X12OrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);
        result.Lines.Should().ContainSingle();
        return result.Lines[0];
    }

    [Fact]
    public async Task ParseAsync_EuThousandsGroupedPrice_ReadsCorrectlyOrFlags()
    {
        // "1.234,56" must read as 1234.56 (last separator ',' is the decimal) — never null,
        // never 1.23456.
        var line = await ParsePo1PriceAsync("1.234,56");

        line.UnitPrice.Should().NotBe(1.23456m, "the thousands group must not be silently dropped");
        (line.UnitPrice == 1234.56m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_EuCommaDecimalPrice_IsNeverReadAs100x()
    {
        // "73,22" must never become 7322.
        var line = await ParsePo1PriceAsync("73,22");

        line.UnitPrice.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.UnitPrice == 73.22m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_InvariantPrices_AreByteUnchanged()
    {
        // The fix must not alter correctly-formatted invariant numbers.
        (await ParsePo1PriceAsync("73.22")).UnitPrice.Should().Be(73.22m);
        (await ParsePo1PriceAsync("1234.56")).UnitPrice.Should().Be(1234.56m);

        var clean = await ParsePo1PriceAsync("73.22");
        clean.NeedsReview.Should().BeFalse();
    }
}
