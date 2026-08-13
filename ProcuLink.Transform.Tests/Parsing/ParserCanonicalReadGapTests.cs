using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// The parser half of the ship-to / bill-to / totals read gap.
///
/// <para>The emitters, the entity columns and the migrations for these fields all shipped in June
/// 2026. The parsers never filled them: <c>new ParsedParty(...)</c> appeared exactly once in the
/// whole solution — inside the AI extractor's projection — so for every structured format
/// <c>ParsedOrder.Parties</c> was null, the ingestion layer's denormalisation found no
/// <c>shipTo</c>/<c>billTo</c> party, and all sixteen ShipTo*/BillTo* columns were written NULL.
/// The visible consequence was that a cXML order in produced a cXML order out with the delivery
/// address deleted, and that the identical content pasted into an email body (which goes through
/// the AI path) captured far more than the structured file did.</para>
///
/// <para><b>Fixture provenance is not uniform, and the difference matters.</b> The cXML and IDoc
/// assertions below run against genuine captured documents that were de-identified in #179/#184 —
/// the data was really present in a real customer order and really was being dropped. The UBL,
/// EDIFACT and X12 assertions run against fixtures authored by hand for this change. Those pin the
/// specification's shape and the parser's reads, but they cannot prove anything about how real
/// senders of those formats actually behave: element ordering, optional-element omission and
/// non-conformant values are all unrepresented, because the author and the reader were the same
/// person. Treat a green UBL/EDIFACT/X12 test as "the read exists and works on a conformant
/// document", never as "this format is proven in production".</para>
///
/// <para><b>CSV, XLSX and PDF are covered at the bottom of this file, and differently.</b> The
/// five parsers above are structured: an element name or a segment qualifier states what a value
/// means. These three carry no such statement — CSV and XLSX are columnar, PDF is free text — so
/// a party is read only where a header or a printed label names the field, and the tests that
/// matter most there are the ones asserting that an unlabelled document still yields NO party.
/// Their fixtures are authored, so the same caveat as UBL/EDIFACT/X12 applies, doubled.</para>
/// </summary>
public class ParserCanonicalReadGapTests
{
    private static string FixturePath(params string[] parts) => Path.Combine(
        new[] { Path.GetDirectoryName(typeof(ParserCanonicalReadGapTests).Assembly.Location)!, "Fixtures" }
            .Concat(parts).ToArray());

    private static Stream OpenFixture(params string[] parts)
    {
        var path = FixturePath(parts);
        File.Exists(path).Should().BeTrue($"fixture must be copied to the test output: {path}");
        return File.OpenRead(path);
    }

    private static ParsedParty Party(ParsedOrder order, string role)
    {
        order.Parties.Should().NotBeNull("the parser must emit parties for a document that states them");
        return order.Parties!.Should().ContainSingle(p => p.Role == role).Subject;
    }

    // ── cXML — REAL captured fixtures ───────────────────────────────────────────

    [Fact]
    public async Task Cxml_RealCoupaFixture_CapturesShipToBillToContactAndStatedTotal()
    {
        // cxml-coupa-orderrequest-sek.cxml states a ShipTo, a BillTo, a Contact and a
        // <Total><Money currency="SEK">3179.15</Money>. Only the @currency attribute used
        // to be read; the amount and all three blocks were discarded.
        await using var stream = OpenFixture("cxml-coupa-orderrequest-sek.cxml");

        var result = await new CxmlOrderParser().ParseAsync(stream, CancellationToken.None);

        result.GrandTotal.Should().Be(3179.15m, "the stated order total is in <Total><Money>");
        result.Currency.Should().Be("SEK");

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Test Buyer Sweden AB");
        shipTo.Street.Should().Be("Testgatan 14");
        shipTo.City.Should().Be("Stockholm");
        shipTo.PostalCode.Should().Be("111 11");
        shipTo.Country.Should().Be("SE", "the ISO code is preferred over the localised label 'Sweden'");
        shipTo.ContactName.Should().Be("Test Användare", "<DeliverTo> is the attention-of person");
        shipTo.Email.Should().Be("test.user@example.com");

        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Test Buyer Sweden AB");
        billTo.Street.Should().Be("Testvägen 56", "the bill-to street differs from the ship-to street");
        billTo.PostalCode.Should().Be("105 55");

        // <Contact role="endUser"> in the OrderRequestHeader — NOT the routing contact that
        // lives under <Header><To><Correspondent>.
        result.ContactName.Should().Be("Test Användare");
        result.ContactEmail.Should().Be("test.user@example.com");
    }

    [Fact]
    public async Task Cxml_RealAribaFixture_CapturesRequestedDeliveryDateAndComposesPhone()
    {
        // real-cxml-1.2-ariba-punchout-mpn-differs.xml states ItemOut@requestedDeliveryDate
        // and a <Phone> composite the canonical model stores as one dialable string.
        await using var stream = OpenFixture("real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        var result = await new CxmlOrderParser().ParseAsync(stream, CancellationToken.None);

        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 7, 17),
            "the offset-local calendar date is kept — converting 03:30-07:00 to UTC would move it to the 18th");
        result.Lines.Should().Contain(l => l.DeliveryDate == new DateOnly(2026, 7, 17));
        result.GrandTotal.Should().Be(164.1m);

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Buyer Service GmbH");
        shipTo.Street.Should().Be("Grünenbeispiel 104-107");
        shipTo.Country.Should().Be("DE");
        shipTo.ContactName.Should().Be("Ship-To Contact",
            "the FIRST <DeliverTo> is the person; the second repeats the company");
        shipTo.Phone.Should().Be("+49 000 00000",
            "the <Phone> composite is flattened — and <Fax>, which has the identical shape, is not read");

        // The bill-to in this document carries a Phone but no Email.
        var billTo = Party(result, "billTo");
        billTo.Street.Should().Be("Beispielwestring 7-Tor 2-WE 2");
        billTo.Email.Should().BeNull();
    }

    [Fact]
    public async Task Cxml_DocumentWithoutAddressBlocks_EmitsNoParties()
    {
        // Anti-vacuity: the assertions above must be detecting real content, not a parser that
        // manufactures a party for every document. Every cXML fixture on disk states a ShipTo,
        // so the negative case has to be stated inline.
        const string noAddresses =
            """
            <cXML payloadID="no-address@example.invalid" timestamp="2026-07-13T10:00:00-00:00">
              <Header>
                <From><Credential domain="NetworkId"><Identity>TestBuyer</Identity></Credential></From>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-NO-ADDRESS" orderDate="2026-07-13" type="new" />
                  <ItemOut quantity="2" lineNumber="1">
                    <ItemID><SupplierPartID>BUY-1</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">10.00</Money></UnitPrice>
                      <Description xml:lang="en">Widget</Description>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(noAddresses));

        var result = await new CxmlOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Parties.Should().BeNull("no address block is stated, so none may be invented");
        result.ContactName.Should().BeNull();
        result.GrandTotal.Should().BeNull("this document states no <Total>");
        result.RequestedDeliveryDate.Should().BeNull("no ItemOut states @requestedDeliveryDate");
    }

    // ── IDoc — REAL captured fixtures ───────────────────────────────────────────

    [Fact]
    public async Task IDoc_RealFixture_CapturesShipToPartyFromPartnerRoleWE()
    {
        // idoc-orders05-11.xml carries E1EDKA1 PARVW=WE with a full address. The parser used to
        // walk straight past it under a comment saying ship-to "has no canonical home".
        await using var stream = OpenFixture("Idoc", "idoc-orders05-11.xml");

        var result = await new IDocOrders05Parser().ParseAsync(stream, CancellationToken.None);

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Ship-To Site IT Contact",
            "NAME1..NAME4 are SAP address name lines and are joined, as they already were for AG/LF");
        shipTo.Street.Should().Be("1 Example Street");
        shipTo.City.Should().Be("Example City");
        shipTo.PostalCode.Should().Be("00000");
        shipTo.Country.Should().Be("IT");
        shipTo.Phone.Should().Be("0000000000");

        // Unchanged behaviour: buyer/supplier still resolve to their own header fields.
        result.BuyerName.Should().Be("Buyer Org IT");
        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 5, 25));
    }

    [Fact]
    public async Task IDoc_FixtureWithoutShipToPartner_EmitsNoShipToParty()
    {
        // Anti-vacuity for the IDoc read: a document with no PARVW=WE segment must produce no
        // shipTo party. idoc-orders05-9.xml is parsed here purely to assert the absence.
        await using var stream = OpenFixture("Idoc", "idoc-orders05-9.xml");

        var result = await new IDocOrders05Parser().ParseAsync(stream, CancellationToken.None);

        // Whatever this document does state, a shipTo party may only appear if PARVW=WE is present.
        var hasWeSegment = (await File.ReadAllTextAsync(FixturePath("Idoc", "idoc-orders05-9.xml")))
            .Contains("<PARVW>WE</PARVW>", StringComparison.Ordinal);
        (result.Parties?.Any(p => p.Role == "shipTo") ?? false).Should().Be(hasWeSegment,
            "the shipTo party must appear exactly when the WE partner segment does");
    }

    // ── UBL — SYNTHETIC fixture (see class doc) ─────────────────────────────────

    [Fact]
    public async Task Ubl_SyntheticFixture_CapturesDeliveryTotalsContactAndSupplierName()
    {
        await using var stream = OpenFixture("Ubl", "ubl-order-parties-eur.xml");

        var result = await new UblOrderParser().ParseAsync(stream, CancellationToken.None);

        // SellerSupplierParty was parsed and then discarded to `_` under a comment claiming it
        // had "no canonical-field home". It has one, and this is it.
        result.SupplierName.Should().Be("Fabrikam Supply AB");

        // cac:AnticipatedMonetaryTotal is the UBL 2.1 ORDER spelling. (cac:LegalMonetaryTotal is
        // the Invoice/OrderResponse spelling; the parser accepts either, since senders do emit it.)
        result.SubTotal.Should().Be(250.00m);
        result.TaxTotal.Should().Be(62.50m);
        result.GrandTotal.Should().Be(312.50m);

        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 6, 15),
            "cac:RequestedDeliveryPeriod/cbc:StartDate is when delivery is asked to begin");

        result.ContactName.Should().Be("Buyer Contact");
        result.ContactEmail.Should().Be("buyer.contact@example.com");
        result.ContactPhone.Should().Be("0000000000");
        result.BuyerTaxId.Should().Be("FI00000000");

        // The ship-to name and address live under DIFFERENT children of cac:Delivery, so a
        // reader that looked at only one of them would produce a half-empty party.
        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse SE");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001");
        shipTo.Country.Should().Be("SE");
        shipTo.ContactName.Should().Be("Warehouse Contact");

        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Contoso Buying OY");
        billTo.Street.Should().Be("1 Example Street, Floor 0",
            "cbc:StreetName and cbc:AdditionalStreetName are joined, not truncated to the first");
        billTo.Country.Should().Be("FI");
        billTo.Vat.Should().Be("FI00000000");
    }

    [Fact]
    public async Task Ubl_DocumentWithoutDeliveryOrTotals_LeavesThemNull()
    {
        // Anti-vacuity: a minimal conformant Order must not acquire a delivery date, totals or
        // a ship-to party the document never stated.
        const string minimal =
            """
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
              <cbc:ID>PO-UBL-MINIMAL</cbc:ID>
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:OrderLine><cac:LineItem>
                <cbc:ID>1</cbc:ID>
                <cbc:Quantity unitCode="EA">1</cbc:Quantity>
                <cac:Price><cbc:PriceAmount currencyID="EUR">1.00</cbc:PriceAmount></cac:Price>
                <cac:Item><cbc:Name>Widget</cbc:Name></cac:Item>
              </cac:LineItem></cac:OrderLine>
            </Order>
            """;
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(minimal));

        var result = await new UblOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Parties.Should().BeNull();
        result.RequestedDeliveryDate.Should().BeNull();
        result.GrandTotal.Should().BeNull();
        result.SubTotal.Should().BeNull();
        result.TaxTotal.Should().BeNull();
        result.ContactName.Should().BeNull();
    }

    // ── EDIFACT — SYNTHETIC fixture (see class doc) ─────────────────────────────

    [Fact]
    public async Task Edifact_SyntheticFixture_CapturesDeliveryAndInvoiceePartiesWithContacts()
    {
        await using var stream = OpenFixture("Edifact", "edifact-orders-parties-eur.edi");

        var result = await new EdifactOrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying OY");
        result.SupplierName.Should().Be("Fabrikam Supply AB", "NAD+SU names the selling party");
        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 6, 15), "DTM+2 is the requested delivery date");

        // NAD+DP (delivery party) is a ship-to. The contact arrives in the CTA/COM segments that
        // FOLLOW the NAD, so a reader matching NAD alone could only ever see a name.
        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse SE");
        shipTo.Street.Should().Be("2 Example Road, Gate 0", "the C059 street composite carries up to four lines");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001");
        shipTo.Country.Should().Be("SE");
        shipTo.ContactName.Should().Be("Warehouse Contact");
        shipTo.Email.Should().Be("warehouse@example.com");
        shipTo.Phone.Should().Be("0000000001");

        // NAD+IV (invoicee) is a bill-to, and its CTA/COM must attach to IT, not to the
        // preceding delivery party.
        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Contoso Finance OY");
        billTo.City.Should().Be("Example Borough");
        billTo.Email.Should().Be("finance@example.com");
        billTo.Phone.Should().BeNull("the invoicee states no COM+TE");
    }

    [Fact]
    public async Task Edifact_MessageWithOnlyABuyerParty_EmitsNoShipToOrBillTo()
    {
        // Anti-vacuity: NAD+BY alone must not become a shipTo or billTo.
        const string edi =
            "UNA:+.? '" +
            "UNB+UNOC:3+0000000000000:14+0000000000001:14+260528:1200+000000001'" +
            "UNH+1+ORDERS:D:96A:UN'" +
            "BGM+220+PO-EDI-MINIMAL+9'" +
            "DTM+137:20260528:102'" +
            "NAD+BY+0000000000000::9++Contoso Buying OY'" +
            "CUX+2:EUR:9'" +
            "LIN+1++BUY-A-0001:IN'" +
            "QTY+21:1:EA'" +
            "PRI+AAA:1.00'" +
            "UNS+S'" +
            "UNT+11+1'" +
            "UNZ+1+000000001'";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edi));

        var result = await new EdifactOrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying OY");
        result.Parties.Should().BeNull();
        result.RequestedDeliveryDate.Should().BeNull();
    }

    // ── X12 — SYNTHETIC fixture (see class doc) ─────────────────────────────────

    [Fact]
    public async Task X12_SyntheticFixture_CapturesShipToAndBillToLoopsWithAddressAndContact()
    {
        await using var stream = OpenFixture("X12", "x12-850-parties-usd.x12");

        var result = await new X12OrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying Inc");
        result.Currency.Should().Be("USD");
        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 6, 15), "DTM*002 is the requested delivery date");

        // N1 opens a LOOP: the address is in the N3/N4 that follow it, and the contact in the PER.
        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse US");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001");
        shipTo.Country.Should().Be("US");
        shipTo.ContactName.Should().Be("Warehouse Contact");
        shipTo.Email.Should().Be("warehouse@example.com");
        shipTo.Phone.Should().Be("0000000001");

        // The bill-to loop's N3 carries a single street line, and it states no PER at all — so the
        // preceding ship-to's contact must NOT bleed into it.
        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Contoso Finance Inc");
        billTo.Street.Should().Be("4 Example Lane");
        billTo.City.Should().Be("Example Borough");
        billTo.ContactName.Should().BeNull();
        billTo.Email.Should().BeNull();
    }

    [Fact]
    public async Task X12_InterchangeWithOnlyABuyerLoop_EmitsNoParties()
    {
        // Anti-vacuity: N1*BY alone must not become a shipTo or billTo, and its N3/N4 must not
        // create a party out of the buyer loop.
        const string nl = "\r\n";
        var edi =
            "ISA*00*          *00*          *ZZ*TESTBUYER      *ZZ*TESTSUPPLIER   *260528*1200*U*00401*000000001*0*P*>~" + nl +
            "GS*PO*TESTBUYER*TESTSUPPLIER*20260528*1200*1*X*004010~" + nl +
            "ST*850*0001~" + nl +
            "BEG*00*NE*PO-X12-MINIMAL**20260528~" + nl +
            "CUR*BY*USD~" + nl +
            "N1*BY*Contoso Buying Inc~" + nl +
            "N3*1 Example Street~" + nl +
            "N4*Example City*NY*00000*US~" + nl +
            "PO1*1*1*EA*1.00**BP*BUY-A-0001~" + nl +
            "CTT*1~" + nl +
            "SE*10*0001~" + nl +
            "GE*1*1~" + nl +
            "IEA*1*000000001~" + nl;
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(edi));

        var result = await new X12OrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying Inc");
        result.Parties.Should().BeNull();
        result.RequestedDeliveryDate.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CSV / XLSX / PDF — the three formats the wedge actually runs on
    //
    // The five parsers above are structured: an element or a segment qualifier states
    // what a value MEANS, so a ship-to can be read without inference. These three cannot
    // do that. CSV and XLSX are columnar and PDF is free text, so the only thing that
    // says "this is the delivery address" is a header or a printed label — and where the
    // document provides none, the correct output is no party at all.
    //
    // Every fixture below is AUTHORED, not captured. They prove the reads exist and that
    // the negative cases stay empty; they prove nothing about how real buyers spell these
    // headers in the wild, because the author and the reader were the same person.
    // ════════════════════════════════════════════════════════════════════════════

    // ── CSV ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_HeaderNamesTheDeliveryAddress_CapturesShipToAndBillTo()
    {
        const string csv =
            "PoNumber,BuyerName,Currency,LineNumber,BuyerItemCode,Quantity,UnitPrice," +
            "Ship To Name,Ship To Street,Ship To City,Ship To Postal Code,Ship To Country,Ship To Contact,Ship To Email," +
            "Bill To Name,Bill To Street,Bill To City,Bill To Postal Code,Bill To Country\n" +
            "PO-CSV-1,Contoso Buying OY,EUR,1,BUY-A-0001,2,4.50," +
            "Contoso Warehouse OY,2 Example Road,Example Town,00001,EE,Warehouse Contact,warehouse@example.com," +
            "Contoso Finance OY,4 Example Lane,Example Borough,00002,EE\n" +
            "PO-CSV-1,Contoso Buying OY,EUR,2,BUY-A-0002,1,9.00,,,,,,,,,,,,\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Lines.Should().HaveCount(2, "the party columns must not disturb line parsing");

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse OY");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001", "a postcode is text — leading zeros are part of it");
        shipTo.Country.Should().Be("EE");
        shipTo.ContactName.Should().Be("Warehouse Contact");
        shipTo.Email.Should().Be("warehouse@example.com");

        // The bill-to is a DIFFERENT address, so a parser that echoed one block into both fails.
        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Contoso Finance OY");
        billTo.Street.Should().Be("4 Example Lane");
        billTo.City.Should().Be("Example Borough");
        billTo.ContactName.Should().BeNull("the file states no bill-to contact column");
    }

    [Fact]
    public async Task Csv_HeaderSpellingVariants_ResolveToTheSameParty()
    {
        // Header text is normalised to letters+digits lowercase, so separators and casing
        // are irrelevant — but the WORDS still have to name the field.
        const string csv =
            "ponumber;linenumber;buyeritemcode;quantity;SHIPTO_NAME;delivery address;Delivery-City\n" +
            "PO-CSV-2;1;BUY-A-0001;2;Contoso Warehouse OY;2 Example Road;Example Town\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse OY");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
    }

    [Fact]
    public async Task Csv_WithNoPartyColumns_EmitsNoParties()
    {
        // Anti-vacuity, and the whole judgement call in one test: a CSV is positional, so
        // where no header names a delivery address there is nothing to read and nothing may
        // be invented from column order.
        const string csv =
            "PoNumber,BuyerName,Currency,LineNumber,BuyerItemCode,Description,Quantity,Unit,UnitPrice\n" +
            "PO-CSV-3,Contoso Buying OY,EUR,1,BUY-A-0001,Widget,2,EA,4.50\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying OY");
        result.Lines.Should().ContainSingle();
        result.Parties.Should().BeNull();
    }

    [Fact]
    public async Task Csv_PartyColumnsPresentButEmpty_EmitsNoParties()
    {
        // A named-but-blank column is not a stated address. Without this the parser would
        // write an order_parties row asserting the document named a delivery party.
        const string csv =
            "PoNumber,LineNumber,BuyerItemCode,Quantity,Ship To Name,Ship To City,Bill To Name\n" +
            "PO-CSV-4,1,BUY-A-0001,2,,,\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Parties.Should().BeNull();
    }

    // ── XLSX ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xlsx_HeaderNamesTheDeliveryAddress_CapturesShipToAndBillTo()
    {
        await using var stream = BuildXlsx(
            new[]
            {
                "PoNumber", "BuyerName", "Currency", "LineNumber", "BuyerItemCode", "Quantity", "UnitPrice",
                "Ship To Name", "ShipToStreet", "Ship To City", "ShipToPostalCode", "ShipToCountry",
                "BillToName", "BillToCity",
            },
            new[]
            {
                new string?[]
                {
                    "PO-XLSX-1", "Contoso Buying OY", "EUR", "1", "BUY-A-0001", "2", "4.50",
                    "Contoso Warehouse OY", "2 Example Road", "Example Town", "00001", "EE",
                    "Contoso Finance OY", "Example Borough",
                },
                new string?[] { "PO-XLSX-1", null, null, "2", "BUY-A-0002", "1", "9.00" },
            });

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Lines.Should().HaveCount(2, "the party columns must not disturb line parsing");

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse OY");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001", "a postcode is text — leading zeros are part of it");
        shipTo.Country.Should().Be("EE");

        var billTo = Party(result, "billTo");
        billTo.Name.Should().Be("Contoso Finance OY");
        billTo.City.Should().Be("Example Borough");
        billTo.Street.Should().BeNull("the sheet states no bill-to street column");
    }

    [Fact]
    public async Task Xlsx_WithNoPartyColumns_EmitsNoParties()
    {
        // Anti-vacuity: a worksheet is columnar, and a column position is not a meaning.
        await using var stream = BuildXlsx(
            new[] { "PoNumber", "BuyerName", "Currency", "LineNumber", "BuyerItemCode", "Quantity", "UnitPrice" },
            new[] { new string?[] { "PO-XLSX-2", "Contoso Buying OY", "EUR", "1", "BUY-A-0001", "2", "4.50" } });

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying OY");
        result.Lines.Should().ContainSingle();
        result.Parties.Should().BeNull();
    }

    [Fact]
    public async Task Xlsx_PartyColumnsPresentButEmpty_EmitsNoParties()
    {
        await using var stream = BuildXlsx(
            new[] { "PoNumber", "LineNumber", "BuyerItemCode", "Quantity", "ShipToName", "ShipToCity" },
            new[] { new string?[] { "PO-XLSX-3", "1", "BUY-A-0001", "2", null, null } });

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.Parties.Should().BeNull();
    }

    [Fact]
    public async Task Xlsx_PreExistingColumnsKeepTheirExactTextMatching()
    {
        // The normalised header lookup is used ONLY by the party columns. "PO Number" must
        // still NOT resolve to PoNumber, or this change would have quietly altered which
        // sheets the parser reads a PO number off.
        await using var stream = BuildXlsx(
            new[] { "PO Number", "LineNumber", "BuyerItemCode", "Quantity", "Ship To Name" },
            new[] { new string?[] { "PO-XLSX-4", "1", "BUY-A-0001", "2", "Contoso Warehouse OY" } });

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().BeNull("'PO Number' is not an alias the existing exact-text map carries");
        Party(result, "shipTo").Name.Should().Be("Contoso Warehouse OY");
    }

    // ── PDF ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_InlineShipToLabels_CaptureTheDeliveryAddress()
    {
        await using var stream = new MemoryStream(PdfOrderParserTests.CreatePdf(
            "PO Number: PO-PDF-1",
            "Order Date: 2026-05-20",
            "Buyer: Contoso Buying OY",
            "Currency: EUR",
            "Ship To: Contoso Warehouse OY",
            "Ship To Address: 2 Example Road",
            "Ship To City: Example Town",
            "Ship To Postal Code: 00001",
            "Ship To Country: EE",
            "Line BuyerItemCode Description Quantity Unit UnitPrice",
            "1 BUY-A-0001 Widget A 2 PCS 4.50"));

        var result = await new PdfOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-PDF-1");
        result.Lines.Should().ContainSingle();

        var shipTo = Party(result, "shipTo");
        shipTo.Name.Should().Be("Contoso Warehouse OY",
            "'Ship To Address:' must not be swallowed by the name pattern");
        shipTo.Street.Should().Be("2 Example Road");
        shipTo.City.Should().Be("Example Town");
        shipTo.PostalCode.Should().Be("00001");
        shipTo.Country.Should().Be("EE");
    }

    [Fact]
    public async Task Pdf_BlockShipToLabel_IsDeliberatelyNotRead()
    {
        // THE JUDGEMENT CALL, pinned. "Ship To" on its own line followed by an unlabelled
        // address is the commonest printed form, and it is the one form this parser refuses:
        // deciding which continuation line is the street and which is the city can only be
        // done by counting lines. A ship-to guessed from layout is worse than none, because
        // once persisted nothing can tell it apart from one the buyer actually stated.
        //
        // If this test ever needs changing, the answer is an operator-supplied mapping — not
        // a line-position heuristic.
        await using var stream = new MemoryStream(PdfOrderParserTests.CreatePdf(
            "PO Number: PO-PDF-2",
            "Buyer: Contoso Buying OY",
            "Currency: EUR",
            "Ship To",
            "Contoso Warehouse OY",
            "2 Example Road",
            "Example Town 00001",
            "Line BuyerItemCode Description Quantity Unit UnitPrice",
            "1 BUY-A-0001 Widget A 2 PCS 4.50"));

        var result = await new PdfOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-PDF-2");
        result.Parties.Should().BeNull("the label carries no value on its own line");
    }

    [Fact]
    public async Task Pdf_WithNoShipToLabel_EmitsNoParties()
    {
        // Anti-vacuity for both PDF tests above.
        await using var stream = new MemoryStream(PdfOrderParserTests.CreatePdf(
            "PO Number: PO-PDF-3",
            "Order Date: 2026-05-20",
            "Buyer: Contoso Buying OY",
            "Currency: EUR",
            "Line BuyerItemCode Description Quantity Unit UnitPrice",
            "1 BUY-A-0001 Widget A 2 PCS 4.50"));

        var result = await new PdfOrderParser().ParseAsync(stream, CancellationToken.None);

        result.BuyerName.Should().Be("Contoso Buying OY");
        result.Lines.Should().ContainSingle();
        result.Parties.Should().BeNull();
    }

    // ── Harness for the authored CSV/XLSX/PDF fixtures ──────────────────────────

    private static MemoryStream BuildXlsx(string[] header, IEnumerable<string?[]> rows)
    {
        var ms = new MemoryStream();
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            for (var c = 0; c < header.Length; c++)
                ws.Cell(1, c + 1).Value = header[c];

            var r = 2;
            foreach (var row in rows)
            {
                for (var c = 0; c < row.Length; c++)
                    if (row[c] is not null)
                        ws.Cell(r, c + 1).Value = row[c];
                r++;
            }
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        return ms;
    }
}
