using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Tests for <see cref="IDocOrders05Parser"/> using five real SAP IDoc ORDERS05
/// purchase orders from the founder's inbound POs (sanitized: emails, person
/// names, phones, addresses, and customer-id GUIDs scrubbed; structural and
/// business fields the parser reads are preserved).
/// </summary>
public class IDocOrders05ParserTests
{
    private static Stream ToStream(string xml) =>
        new MemoryStream(Encoding.UTF8.GetBytes(xml));

    private static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(IDocOrders05ParserTests).Assembly.Location)!,
        "Fixtures", "Idoc", name);

    private static Stream OpenFixture(string name) => File.OpenRead(FixturePath(name));

    // ── CanParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrueForXmlOnly()
    {
        var parser = new IDocOrders05Parser();
        parser.CanParse(".xml").Should().BeTrue();
        parser.CanParse(".XML").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".pdf").Should().BeFalse();
        parser.CanParse(".cxml").Should().BeFalse();
    }

    // ── Content detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task IsIdocOrders05Document_TrueForOrders05Root()
    {
        await using var stream = OpenFixture("idoc-orders05-9.xml");
        IDocOrders05Parser.IsIdocOrders05Document(stream).Should().BeTrue();
    }

    [Fact]
    public void IsIdocOrders05Document_FalseForCxml()
    {
        using var stream = ToStream("<cXML><Request/></cXML>");
        IDocOrders05Parser.IsIdocOrders05Document(stream).Should().BeFalse();
    }

    [Fact]
    public void IsIdocOrders05Document_FalseForUbl()
    {
        using var stream = ToStream(
            "<Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"/>");
        IDocOrders05Parser.IsIdocOrders05Document(stream).Should().BeFalse();
    }

    [Fact]
    public void IsIdocOrders05Document_RestoresStreamPosition()
    {
        using var stream = ToStream("<ORDERS05><IDOC/></ORDERS05>");
        IDocOrders05Parser.IsIdocOrders05Document(stream);
        stream.Position.Should().Be(0);
    }

    // ── Fixture: single line, numeric CURCY overridden by SUNIT ─────────────────

    [Fact]
    public async Task ParseAsync_New9_SingleLine_MapsHeaderAndLine()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-9.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("0005472513");      // E1EDK02 QUALF=001 BELNR
        result.Currency.Should().Be("EUR");              // E1EDS01 SUNIT (CURCY=704 numeric is ignored)
        result.BuyerName.Should().Be("Buyer Org ES");    // E1EDKA1 PARVW=AG ORGTX
        result.SupplierName.Should().BeNull();           // LF party carries no name fields
        result.DocumentType.Should().Be("order");
        result.OrderDate.Should().Be(new DateTime(2026, 6, 3)); // E1EDK02 DATUM
        result.GrandTotal.Should().Be(149.49m);          // E1EDS01 SUMME

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.LineNumber.Should().Be(10);                 // POSEX 00010
        line.Quantity.Should().Be(1m);                   // MENGE 1.000
        line.Unit.Should().Be("PCE");                    // MENEE
        line.UnitPrice.Should().Be(149.49m);             // VPREI
        line.LineAmount.Should().Be(149.49m);            // NETWR
        line.BuyerItemCode.Should().Be("000000000030001746"); // E1EDP19 QUALF=001 IDTNR (no QUALF=002)
        line.Description.Should().Be("TSPAD WIFI 4G 128GB 10,2 KIT SPANISH"); // KTEXT (no line-level E1EDPT1)
    }

    // ── Fixture: 4 lines, numeric CURCY=704 must defer to SUNIT=EUR ─────────────

    [Fact]
    public async Task ParseAsync_New710_MultiLine_PrefersSunitCurrency()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-710.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("0005474036");
        result.Currency.Should().Be("EUR");
        result.GrandTotal.Should().Be(526.6m);           // E1EDS01 SUMME

        result.Lines.Should().HaveCount(4);
        result.Lines.Select(l => l.LineNumber).Should().Equal(10, 20, 30, 40); // POSEX

        result.Lines[0].UnitPrice.Should().Be(507.88m);
        result.Lines[0].BuyerItemCode.Should().Be("000000000030002635");
        result.Lines[0].Description.Should().Be("TSPHONE 16E");

        result.Lines[1].UnitPrice.Should().Be(12.9m);
        result.Lines[1].Description.Should().Be("PROTECTOR TSPHONE");

        result.Lines[3].UnitPrice.Should().Be(0.01m);
        result.Lines[3].Description.Should().Be("ENROLAMIENTO EN DEP");
    }

    // ── Fixture: QUALF=002 buyer codes + E1EDPT1/E1EDPT2 continuation text ───────

    [Fact]
    public async Task ParseAsync_New11_UsesQualf002BuyerCodeAndConcatenatesLineText()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-11.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("4501450099");
        result.Currency.Should().Be("EUR");
        result.BuyerName.Should().Be("Buyer Org IT");
        result.Lines.Should().HaveCount(5);

        var first = result.Lines[0];
        first.LineNumber.Should().Be(10);
        first.Quantity.Should().Be(50m);
        first.Unit.Should().Be("EA");
        first.UnitPrice.Should().Be(1.05m);
        first.LineAmount.Should().Be(52.5m);
        first.BuyerItemCode.Should().Be("15728463"); // E1EDP19 QUALF=002 IDTNR
        first.Description.Should().Be(
            "Lamna Solution Economy - Cavo di rete -RJ-45 (M) a RJ-45 (M) - 1.5 m - UTP - CAT 6 - blu");

        // Line 4 has a 5-line E1EDPT2 description block — all continuation lines joined.
        var fourth = result.Lines[3];
        fourth.LineNumber.Should().Be(40);
        fourth.BuyerItemCode.Should().Be("20612144");
        fourth.Description.Should().StartWith("Woodgrove 1.5m CAT6 Ethernet Cable");
        fourth.Description.Should().EndWith("arancione");
    }

    // ── Fixture: empty self-closing E1EDP19 + LF BNAME used as supplier name ─────

    [Fact]
    public async Task ParseAsync_New12_HandlesEmptyE1EDP19AndSupplierName()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-12.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("4501453089");
        result.Currency.Should().Be("EUR");
        result.BuyerName.Should().Be("Buyer Org FR");
        result.SupplierName.Should().Be("contact@example.com"); // LF BNAME (only name field present)

        result.Lines.Should().HaveCount(2);
        result.Lines[0].BuyerItemCode.Should().Be("11097719");
        result.Lines[0].UnitPrice.Should().Be(19.33m);
        result.Lines[0].Description.Should().Be(
            "Wingtip Swift Drive - USB flash drive - 32 GB - USB 3.0 - for Nod Compact Unit 12 Pro Kit - NCU12WSKi3");

        // Second line carries an empty <E1EDP19 SEGMENT="1" /> — must not throw.
        result.Lines[1].BuyerItemCode.Should().Be("153023");
        result.Lines[1].Description.Should().Be("Delivery Charges");
    }

    // ── Fixture: 3 lines ────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_New13_MapsAllLines()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-13.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("4501453008");
        result.BuyerName.Should().Be("Buyer Org IT");
        result.Lines.Should().HaveCount(3);
        result.Lines.Select(l => l.LineNumber).Should().Equal(10, 20, 30);

        var third = result.Lines[2];
        third.Quantity.Should().Be(100m);
        third.UnitPrice.Should().Be(3.09m);
        third.LineAmount.Should().Be(309m);
        third.BuyerItemCode.Should().Be("32281494");
    }

    // ── Validation / error paths ────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_NonOrders05Root_Throws()
    {
        var parser = new IDocOrders05Parser();
        var act = async () => await parser.ParseAsync(
            ToStream("<cXML><Request/></cXML>"), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*ORDERS05*");
    }

    [Fact]
    public async Task ParseAsync_NoIdocSegment_Throws()
    {
        var parser = new IDocOrders05Parser();
        var act = async () => await parser.ParseAsync(
            ToStream("<ORDERS05></ORDERS05>"), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*IDOC*");
    }

    [Fact]
    public async Task ParseAsync_NoPoNumber_Throws()
    {
        // IDOC with a line but no E1EDK02 QUALF=001 BELNR and no E1EDK01 BELNR.
        const string xml = """
            <ORDERS05><IDOC BEGIN="1">
              <E1EDK01 SEGMENT="1"><CURCY>EUR</CURCY></E1EDK01>
              <E1EDP01 SEGMENT="1">
                <POSEX>00010</POSEX><MENGE>1</MENGE><MENEE>EA</MENEE><VPREI>1</VPREI>
                <E1EDP19 SEGMENT="1"><QUALF>002</QUALF><IDTNR>X1</IDTNR></E1EDP19>
              </E1EDP01>
            </IDOC></ORDERS05>
            """;
        var parser = new IDocOrders05Parser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*PO number*");
    }

    [Fact]
    public async Task ParseAsync_NoLines_Throws()
    {
        const string xml = """
            <ORDERS05><IDOC BEGIN="1">
              <E1EDK02 SEGMENT="1"><QUALF>001</QUALF><BELNR>PO-1</BELNR></E1EDK02>
            </IDOC></ORDERS05>
            """;
        var parser = new IDocOrders05Parser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<IDocParseException>().WithMessage("*E1EDP01*");
    }

    [Fact]
    public async Task ParseAsync_PoNumberFallsBackToE1EDK01Belnr()
    {
        // No E1EDK02 at all — PO number must come from E1EDK01 BELNR.
        const string xml = """
            <ORDERS05><IDOC BEGIN="1">
              <E1EDK01 SEGMENT="1"><CURCY>EUR</CURCY><BELNR>4500000001</BELNR></E1EDK01>
              <E1EDP01 SEGMENT="1">
                <POSEX>00010</POSEX><MENGE>2</MENGE><MENEE>EA</MENEE><VPREI>5</VPREI><NETWR>10</NETWR>
                <E1EDP19 SEGMENT="1"><QUALF>002</QUALF><IDTNR>ABC</IDTNR></E1EDP19>
              </E1EDP01>
            </IDOC></ORDERS05>
            """;
        var parser = new IDocOrders05Parser();
        await using var stream = ToStream(xml);

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("4500000001");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().ContainSingle();
        result.Lines[0].BuyerItemCode.Should().Be("ABC");
    }

    // ── B3: locale-aware decimal parsing (shared NumberParsing reader) ───────────
    // SAP IDoc emits invariant-locale numbers ('.' decimal, ',' groups thousands). The old
    // naive ParseDecimal only swapped ','→'.' when no '.' was present, so EU "1.234,56"
    // collapsed to 1.23456 (group dropped) and never flagged it. Now read correctly OR
    // flagged NeedsReview — never silently wrong. Correctly-formatted numbers are unchanged.

    /// <summary>
    /// Builds a one-line ORDERS05 IDOC whose VPREI (unit price) and MENGE (quantity)
    /// carry the supplied raw tokens, so the decimal reader can be exercised directly.
    /// </summary>
    private static string OneLineIdoc(string vprei, string menge = "1") => $"""
        <ORDERS05><IDOC BEGIN="1">
          <E1EDK02 SEGMENT="1"><QUALF>001</QUALF><BELNR>PO-DEC-1</BELNR></E1EDK02>
          <E1EDP01 SEGMENT="1">
            <POSEX>00010</POSEX><MENGE>{menge}</MENGE><MENEE>EA</MENEE><VPREI>{vprei}</VPREI>
            <E1EDP19 SEGMENT="1"><QUALF>002</QUALF><IDTNR>ITEM-1</IDTNR></E1EDP19>
          </E1EDP01>
        </IDOC></ORDERS05>
        """;

    private static async Task<ParsedOrderLine> ParseIdocPriceAsync(string vprei)
    {
        var parser = new IDocOrders05Parser();
        await using var stream = ToStream(OneLineIdoc(vprei));
        var result = await parser.ParseAsync(stream, CancellationToken.None);
        result.Lines.Should().ContainSingle();
        return result.Lines[0];
    }

    [Fact]
    public async Task ParseAsync_EuThousandsGroupedPrice_ReadsCorrectlyOrFlags()
    {
        // "1.234,56" must read as 1234.56 (last separator ',' is the decimal) — never null,
        // never 1.23456 (the old reader silently dropped the thousands group).
        var line = await ParseIdocPriceAsync("1.234,56");

        line.UnitPrice.Should().NotBe(1.23456m, "the thousands group must not be silently dropped");
        line.UnitPrice.Should().NotBeNull("a readable price must never collapse to null");
        (line.UnitPrice == 1234.56m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_EuCommaDecimalPrice_IsNeverReadAs100x()
    {
        // "73,22" must never become 7322.
        var line = await ParseIdocPriceAsync("73,22");

        line.UnitPrice.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.UnitPrice == 73.22m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_InvariantPrices_AreByteUnchangedAndNotFlagged()
    {
        // The fix must not alter correctly-formatted invariant numbers, nor flag them.
        var a = await ParseIdocPriceAsync("73.22");
        a.UnitPrice.Should().Be(73.22m);
        a.NeedsReview.Should().BeFalse();
        a.ReviewReason.Should().BeNull();

        var b = await ParseIdocPriceAsync("1234.56");
        b.UnitPrice.Should().Be(1234.56m);
        b.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_SingleSeparatorAmbiguousPrice_IsCorrectOrFlagged_NeverSilentlyWrong()
    {
        // "1.234" is single-dot, 3 trailing digits — could be 1.234 (decimal) or 1234
        // (EU thousands group). Under european:false the reader takes it as the invariant
        // decimal 1.234; whichever it chooses it must be a real reading, never silently wrong.
        var line = await ParseIdocPriceAsync("1.234");

        (line.UnitPrice == 1.234m || line.UnitPrice == 1234m || line.NeedsReview)
            .Should().BeTrue("the value must be a defensible reading or flagged for review");
    }

    [Fact]
    public async Task ParseAsync_AmbiguousQuantity_FlagsLineForReview()
    {
        // A stray exponent in the quantity is the silent-corruption class the guard rejects.
        var parser = new IDocOrders05Parser();
        await using var stream = ToStream(OneLineIdoc(vprei: "1.05", menge: "1.5e2"));
        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.Lines.Should().ContainSingle();
        result.Lines[0].NeedsReview.Should().BeTrue("an exponent token must not be silently coerced into a quantity");
        result.Lines[0].ReviewReason.Should().NotBeNull();
    }

    // ── OrderParserFactory .xml disambiguation ──────────────────────────────────

    [Fact]
    public void Factory_RoutesOrders05XmlToIDocParser()
    {
        var idoc = new IDocOrders05Parser();
        var factory = new OrderParserFactory(
            new IPurchaseOrderParser[] { new CxmlOrderParser(), new UblOrderParser(), idoc });

        using var stream = ToStream("<ORDERS05><IDOC/></ORDERS05>");
        factory.GetParser(".xml", stream).Should().BeSameAs(idoc);
    }

    [Fact]
    public void Factory_StillRoutesCxmlAndUblXml()
    {
        var factory = new OrderParserFactory(
            new IPurchaseOrderParser[] { new CxmlOrderParser(), new UblOrderParser(), new IDocOrders05Parser() });

        using var cxml = ToStream("<cXML><Request/></cXML>");
        factory.GetParser(".xml", cxml).Should().BeOfType<CxmlOrderParser>();

        using var ubl = ToStream(
            "<Order xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Order-2\"/>");
        factory.GetParser(".xml", ubl).Should().BeOfType<UblOrderParser>();
    }
}
