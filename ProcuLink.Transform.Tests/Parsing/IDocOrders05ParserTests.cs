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
        line.Description.Should().Be("REDACTED-ORDER-DATA"); // KTEXT (no line-level E1EDPT1)
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
        result.Lines[0].Description.Should().Be("REDACTED-ORDER-DATA");

        result.Lines[1].UnitPrice.Should().Be(12.9m);
        result.Lines[1].Description.Should().Be("REDACTED-PARTY");

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
            "REDACTED-ORDER-DATA UTP - CAT 6 - blu");

        // Line 4 has a 5-line E1EDPT2 description block — all continuation lines joined.
        var fourth = result.Lines[3];
        fourth.LineNumber.Should().Be(40);
        fourth.BuyerItemCode.Should().Be("20612144");
        fourth.Description.Should().StartWith("REDACTED-ORDER-DATA");
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
            "REDACTED-ORDER-DATA REDACTED-ORDER-DATA");

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
