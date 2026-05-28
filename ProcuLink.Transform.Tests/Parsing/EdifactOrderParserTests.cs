using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class EdifactOrderParserTests
{
    // EDIFACT messages use ' as the segment terminator. Tests below use the
    // EDIFACT default delimiter set (':' component, '+' element, '?' release,
    // '\'' segment) and a CRLF after each terminator for readability only —
    // the parser strips control whitespace between segments.
    private const string NL = "\r\n";

    private static Stream ToStream(string edi) =>
        new MemoryStream(Encoding.UTF8.GetBytes(edi));

    private static string MinimalHeader(
        string poNumber  = "PO-D96A-001",
        string orderDate = "20240115",
        string currency  = "EUR") =>
        "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
        "UNH+1+ORDERS:D:96A:UN'" + NL +
        $"BGM+220+{poNumber}+9'" + NL +
        $"DTM+137:{orderDate}:102'" + NL +
        $"CUX+2:{currency}:9'" + NL +
        "NAD+BY+5412345678901::9++Acme Buyer Ltd'" + NL +
        "NAD+SU+8712345670013::9++Beta Supplier OU'" + NL;

    private static string Trailer =>
        "UNS+S'" + NL +
        "UNT+11+1'" + NL +
        "UNZ+1+1'" + NL;

    // ── CanParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrueForEdi()
    {
        var parser = new EdifactOrderParser();
        parser.CanParse(".edi").Should().BeTrue();
        parser.CanParse(".EDI").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".xml").Should().BeFalse();
        parser.CanParse(".txt").Should().BeFalse();  // factory dispatch is extension-only today
    }

    [Fact]
    public void LooksLikeEdifact_RecognisesUnaAndUnbHeaders()
    {
        EdifactOrderParser.LooksLikeEdifact("UNB+UNOC:3+...").Should().BeTrue();
        EdifactOrderParser.LooksLikeEdifact("UNA:+.? 'UNB+...").Should().BeTrue();
        EdifactOrderParser.LooksLikeEdifact("  \r\nUNB+UNOC:3").Should().BeTrue();
        EdifactOrderParser.LooksLikeEdifact("<?xml version=\"1.0\"?>").Should().BeFalse();
        EdifactOrderParser.LooksLikeEdifact("").Should().BeFalse();
    }

    [Fact]
    public void IsEdifactContentType_AcceptsOfficialAndTolerantSynonym()
    {
        EdifactOrderParser.IsEdifactContentType("application/edifact").Should().BeTrue();
        EdifactOrderParser.IsEdifactContentType("application/edi-x12").Should().BeTrue();
        EdifactOrderParser.IsEdifactContentType("text/csv").Should().BeFalse();
        EdifactOrderParser.IsEdifactContentType(null).Should().BeFalse();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_SingleOrdersMessageWithThreeLines_MapsAllFields()
    {
        var edi =
            MinimalHeader(poNumber: "PO-HAPPY-001", orderDate: "20240115", currency: "EUR") +
            "LIN+1++ITEM-A:IN'" + NL +
            "IMD+F+ANM+:::Widget Type A'" + NL +
            "QTY+21:100:PCE'" + NL +
            "PRI+AAA:12.50'" + NL +
            "LIN+2++ITEM-B:IN'" + NL +
            "IMD+F+ANM+:::Bolt M8x40'" + NL +
            "QTY+21:250:PCE'" + NL +
            "PRI+AAA:0.45'" + NL +
            "LIN+3++ITEM-C:IN'" + NL +
            "IMD+F+ANM+:::Steel sheet'" + NL +
            "QTY+21:5:PCE'" + NL +
            "PRI+AAA:89.00'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.PoNumber.Should().Be("PO-HAPPY-001");
        result.OrderDate.Should().Be(new DateTime(2024, 1, 15));
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(3);

        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("ITEM-A");
        result.Lines[0].Description.Should().Be("Widget Type A");
        result.Lines[0].Quantity.Should().Be(100m);
        result.Lines[0].Unit.Should().Be("PCE");
        result.Lines[0].UnitPrice.Should().Be(12.50m);

        result.Lines[1].BuyerItemCode.Should().Be("ITEM-B");
        result.Lines[1].Quantity.Should().Be(250m);
        result.Lines[1].UnitPrice.Should().Be(0.45m);

        result.Lines[2].BuyerItemCode.Should().Be("ITEM-C");
        result.Lines[2].Quantity.Should().Be(5m);
    }

    [Fact]
    public async Task ParseAsync_MixedQtyUnits_PreservesPceAndKgm()
    {
        var edi =
            MinimalHeader() +
            "LIN+1++WIDGET-PCE:IN'" + NL +
            "QTY+21:10:PCE'" + NL +
            "PRI+AAA:5.00'" + NL +
            "LIN+2++STEEL-KGM:IN'" + NL +
            "QTY+21:125.5:KGM'" + NL +
            "PRI+AAA:1.20'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.Lines.Should().HaveCount(2);
        result.Lines[0].Unit.Should().Be("PCE");
        result.Lines[0].Quantity.Should().Be(10m);
        result.Lines[1].Unit.Should().Be("KGM");
        result.Lines[1].Quantity.Should().Be(125.5m);
    }

    // ── DTM extraction ────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_Dtm137_PopulatesOrderDate()
    {
        // DTM 137 = document/order date; DTM 2 = delivery date (we only consume 137).
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240301:0900+1'" + NL +
            "UNH+1+ORDERS:D:96A:UN'" + NL +
            "BGM+220+PO-DTM-A+9'" + NL +
            "DTM+137:20240301:102'" + NL +
            "DTM+2:20240320:102'" + NL +
            "NAD+BY+5412345678901::9++Acme Buyer Ltd'" + NL +
            "LIN+1++X:IN'" + NL +
            "QTY+21:1:PCE'" + NL +
            "PRI+AAA:1.00'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.OrderDate.Should().Be(new DateTime(2024, 3, 1));
        result.PoNumber.Should().Be("PO-DTM-A");
    }

    // ── NAD extraction ────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_NadBy_PopulatesBuyerName_NadSuIgnoredForBuyer()
    {
        // Confirm that BY is picked even when SU appears first in the message
        // (real EDIFACT messages don't guarantee ordering).
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
            "UNH+1+ORDERS:D:96A:UN'" + NL +
            "BGM+220+PO-NAD-001+9'" + NL +
            "DTM+137:20240115:102'" + NL +
            "NAD+SU+8712345670013::9++Beta Supplier OU'" + NL +
            "NAD+BY+5412345678901::9++Acme Buyer Ltd'" + NL +
            "LIN+1++X:IN'" + NL +
            "QTY+21:1:PCE'" + NL +
            "PRI+AAA:1.00'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.BuyerName.Should().Be("Acme Buyer Ltd");
    }

    // ── UNA / custom delimiters ───────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_CustomUnaDelimiters_ParsesCorrectly()
    {
        // UNA defines: '|' element, ';' component, '.' decimal, '?' release, ' ' reserved, '~' segment.
        // (The 9-char UNA layout is exactly: UNA + comp + elem + dec + release + reserved + segment.)
        var edi =
            "UNA;|.? ~" +
            "UNB|UNOC;3|SENDER;14|RECEIVER;14|240115;0900|1~" +
            "UNH|1|ORDERS;D;96A;UN~" +
            "BGM|220|PO-UNA-001|9~" +
            "DTM|137;20240115;102~" +
            "CUX|2;USD;9~" +
            "NAD|BY|||Acme Buyer Ltd~" +
            "LIN|1||CUSTOM-1;IN~" +
            "QTY|21;7;PCE~" +
            "PRI|AAA;3.50~" +
            "UNS|S~" +
            "UNT|9|1~" +
            "UNZ|1|1~";

        var parser = new EdifactOrderParser();
        var result = await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        result.PoNumber.Should().Be("PO-UNA-001");
        result.Currency.Should().Be("USD");
        result.BuyerName.Should().Be("Acme Buyer Ltd");
        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("CUSTOM-1");
        result.Lines[0].Quantity.Should().Be(7m);
        result.Lines[0].Unit.Should().Be("PCE");
        result.Lines[0].UnitPrice.Should().Be(3.50m);
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_EmptyInput_ThrowsEdifactParseException()
    {
        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(""), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>()
                 .WithMessage("*empty*");
    }

    [Fact]
    public async Task ParseAsync_WhitespaceOnlyInput_ThrowsEdifactParseException()
    {
        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream("   \r\n\t  "), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>()
                 .WithMessage("*empty*");
    }

    [Fact]
    public async Task ParseAsync_MissingUnh_ThrowsEdifactParseException()
    {
        // BGM present but no UNH — malformed.
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
            "BGM+220+PO-NOUNH+9'" + NL +
            "DTM+137:20240115:102'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>()
                 .WithMessage("*UNH*");
    }

    [Fact]
    public async Task ParseAsync_NonOrdersMessageType_ThrowsEdifactParseException()
    {
        // Same envelope but message type INVOIC, not ORDERS.
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
            "UNH+1+INVOIC:D:96A:UN'" + NL +
            "BGM+380+INV-001+9'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>()
                 .WithMessage("*ORDERS*");
    }

    [Fact]
    public async Task ParseAsync_MissingBgm_ThrowsEdifactParseException()
    {
        // UNH but no BGM — malformed.
        var edi =
            "UNB+UNOC:3+SENDER:14+RECEIVER:14+240115:0900+1'" + NL +
            "UNH+1+ORDERS:D:96A:UN'" + NL +
            "DTM+137:20240115:102'" + NL +
            Trailer;

        var parser = new EdifactOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(edi), CancellationToken.None);

        await act.Should().ThrowAsync<EdifactParseException>()
                 .WithMessage("*BGM*");
    }
}
