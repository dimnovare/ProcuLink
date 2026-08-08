using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class UblOrderParserTests
{
    private const string UblOrderNs = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private const string CbcNs      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string CacNs      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private static string FullSampleUbl(
        string orderId         = "PO-12345",
        string issueDate       = "2026-05-28",
        string currency        = "EUR",
        string? customizationId = null,
        string buyerName       = "Acme Buyer Ltd",
        string sellerName      = "Globex Supplier OY")
    {
        var customizationLine = customizationId is null
            ? ""
            : $"  <cbc:CustomizationID>{customizationId}</cbc:CustomizationID>";

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="{UblOrderNs}"
                   xmlns:cbc="{CbcNs}"
                   xmlns:cac="{CacNs}">
            {customizationLine}
              <cbc:ID>{orderId}</cbc:ID>
              <cbc:IssueDate>{issueDate}</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>{currency}</cbc:DocumentCurrencyCode>
              <cac:BuyerCustomerParty>
                <cac:Party>
                  <cac:PartyName><cbc:Name>{buyerName}</cbc:Name></cac:PartyName>
                </cac:Party>
              </cac:BuyerCustomerParty>
              <cac:SellerSupplierParty>
                <cac:Party>
                  <cac:PartyName><cbc:Name>{sellerName}</cbc:Name></cac:PartyName>
                </cac:Party>
              </cac:SellerSupplierParty>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">10</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="{currency}">125.00</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>Widget Type A</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>SUP-ABC-001</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>2</cbc:ID>
                  <cbc:Quantity unitCode="KG">5</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="{currency}">50.00</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>Widget Type B</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>SUP-XYZ-002</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>3</cbc:ID>
                  <cbc:Quantity unitCode="PCS">3</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="{currency}">99.99</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>Widget Type C</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>SUP-DEF-003</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;
    }

    private static Stream ToStream(string xml) =>
        new MemoryStream(Encoding.UTF8.GetBytes(xml));

    // ── CanParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrueForXmlAndUbl()
    {
        var parser = new UblOrderParser();
        parser.CanParse(".xml").Should().BeTrue();
        parser.CanParse(".XML").Should().BeTrue();
        parser.CanParse(".ubl").Should().BeTrue();
        parser.CanParse(".UBL").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".pdf").Should().BeFalse();
        parser.CanParse(".cxml").Should().BeFalse();
    }

    // ── Happy path: UBL 2.1 with three lines ──────────────────────────────────

    [Fact]
    public async Task ParseAsync_ValidUbl21Order_MapsHeaderAndThreeLines()
    {
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(FullSampleUbl(orderId: "PO-12345", issueDate: "2026-05-28", currency: "EUR")),
            CancellationToken.None);

        result.PoNumber.Should().Be("PO-12345");
        result.OrderDate.Should().Be(new DateTime(2026, 5, 28));
        result.Currency.Should().Be("EUR");
        result.BuyerName.Should().Be("Acme Buyer Ltd");

        result.Lines.Should().HaveCount(3);
        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("SUP-ABC-001");
        result.Lines[0].Description.Should().Be("Widget Type A");
        result.Lines[0].Quantity.Should().Be(10m);
        result.Lines[0].Unit.Should().Be("EA");
        result.Lines[0].UnitPrice.Should().Be(125.00m);

        result.Lines[1].LineNumber.Should().Be(2);
        result.Lines[1].BuyerItemCode.Should().Be("SUP-XYZ-002");
        result.Lines[1].Quantity.Should().Be(5m);
        result.Lines[1].Unit.Should().Be("KG");

        result.Lines[2].LineNumber.Should().Be(3);
        result.Lines[2].BuyerItemCode.Should().Be("SUP-DEF-003");
        result.Lines[2].Quantity.Should().Be(3m);
        result.Lines[2].Unit.Should().Be("PCS");
    }

    // ── Peppol BIS Order 3.x variant ──────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_PeppolBisOrder3_ParsesAsUbl()
    {
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(FullSampleUbl(
                orderId:         "PEPPOL-001",
                customizationId: "urn:fdc:peppol.eu:poacc:trns:order:3",
                currency:        "EUR")),
            CancellationToken.None);

        result.PoNumber.Should().Be("PEPPOL-001");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(3);
        result.Lines[0].BuyerItemCode.Should().Be("SUP-ABC-001");
    }

    // ── Party extraction ──────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_BuyerCustomerParty_NameExtractedIntoBuyerName()
    {
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(FullSampleUbl(buyerName: "Exemplar Jaekaubandus OÜ")),
            CancellationToken.None);

        result.BuyerName.Should().Be("Exemplar Jaekaubandus OÜ");
    }

    [Fact]
    public async Task ParseAsync_SellerSupplierParty_ParsesWithoutErrorEvenWhenPresent()
    {
        // SellerSupplierParty is parsed for future supplier-resolution use but does NOT
        // overwrite BuyerName on ParsedOrder. This guards against an accidental swap.
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(FullSampleUbl(
                buyerName:  "Buyer Co AS",
                sellerName: "Seller Logistics OY")),
            CancellationToken.None);

        result.BuyerName.Should().Be("Buyer Co AS");
        result.BuyerName.Should().NotBe("Seller Logistics OY");
    }

    // ── LineItem extraction: Item/Name, Item/SellersItemIdentification, Quantity, Price ──

    [Fact]
    public async Task ParseAsync_LineItem_ExtractsItemNameAndSellersIdAndQuantityAndPrice()
    {
        // BuyersItemIdentification takes priority over SellersItemIdentification when present
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-LINE</cbc:ID>
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">7</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="EUR">42.50</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>Gadget</cbc:Name>
                    <cac:BuyersItemIdentification><cbc:ID>BUY-001</cbc:ID></cac:BuyersItemIdentification>
                    <cac:SellersItemIdentification><cbc:ID>SUP-001</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;

        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.LineNumber.Should().Be(1);
        line.BuyerItemCode.Should().Be("BUY-001");           // BuyersItemIdentification wins
        line.Description.Should().Be("Gadget");
        line.Quantity.Should().Be(7m);
        line.Unit.Should().Be("EA");
        line.UnitPrice.Should().Be(42.50m);
    }

    [Fact]
    public async Task ParseAsync_LineItemWithoutBuyersItemId_FallsBackToSellersItemId()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-FALLBACK</cbc:ID>
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">1</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="EUR">10.00</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>No-Buyer-Id Item</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>SELLER-ONLY-99</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;

        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("SELLER-ONLY-99");
    }

    // ── Currency extraction ───────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_DocumentCurrencyCode_IsUppercasedAndUsed()
    {
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(FullSampleUbl(currency: "usd")),
            CancellationToken.None);

        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task ParseAsync_MissingHeaderCurrency_FallsBackToPriceAmountCurrencyId()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-NOCUR</cbc:ID>
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">1</cbc:Quantity>
                  <cac:Price>
                    <cbc:PriceAmount currencyID="GBP">10.00</cbc:PriceAmount>
                  </cac:Price>
                  <cac:Item>
                    <cbc:Name>Item</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>X</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;

        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.Currency.Should().Be("GBP");
    }

    // ── Malformed / not-UBL inputs ────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_MalformedXml_ThrowsUblParseException()
    {
        const string malformed = "<?xml version=\"1.0\"?><Order><cbc:ID>oops"; // unterminated tags

        var parser = new UblOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(malformed), CancellationToken.None);

        await act.Should().ThrowAsync<UblParseException>()
                 .WithMessage("*could not be parsed*");
    }

    [Fact]
    public async Task ParseAsync_NonUblRootElement_ThrowsUblParseException()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PurchaseOrder><Header/></PurchaseOrder>
            """;

        var parser = new UblOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        await act.Should().ThrowAsync<UblParseException>()
                 .WithMessage("*root*Order*");
    }

    [Fact]
    public async Task ParseAsync_OrderRootWithWrongNamespace_ThrowsUblParseException()
    {
        // <Order> root but the namespace is NOT UBL Order-2 — must reject
        const string xml = """
            <?xml version="1.0"?>
            <Order xmlns="http://example.com/not-ubl"><ID>PO-1</ID></Order>
            """;

        var parser = new UblOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        await act.Should().ThrowAsync<UblParseException>()
                 .WithMessage("*namespace*");
    }

    [Fact]
    public async Task ParseAsync_MissingHeaderId_ThrowsUblParseException()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
              <cac:OrderLine>
                <cac:LineItem>
                  <cbc:ID>1</cbc:ID>
                  <cbc:Quantity unitCode="EA">1</cbc:Quantity>
                  <cac:Price><cbc:PriceAmount currencyID="EUR">10</cbc:PriceAmount></cac:Price>
                  <cac:Item>
                    <cbc:Name>X</cbc:Name>
                    <cac:SellersItemIdentification><cbc:ID>S-1</cbc:ID></cac:SellersItemIdentification>
                  </cac:Item>
                </cac:LineItem>
              </cac:OrderLine>
            </Order>
            """;

        var parser = new UblOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        await act.Should().ThrowAsync<UblParseException>()
                 .WithMessage("*cbc:ID*");
    }

    [Fact]
    public async Task ParseAsync_NoOrderLine_ThrowsUblParseException()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-EMPTY</cbc:ID>
              <cbc:IssueDate>2026-05-28</cbc:IssueDate>
              <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
            </Order>
            """;

        var parser = new UblOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        await act.Should().ThrowAsync<UblParseException>()
                 .WithMessage("*OrderLine*");
    }

    // ── Static content-peek helper ────────────────────────────────────────────

    [Fact]
    public void IsUblOrderDocument_ReturnsTrueForUblOrderDocument()
    {
        using var stream = ToStream(FullSampleUbl());
        UblOrderParser.IsUblOrderDocument(stream).Should().BeTrue();
        // Stream position must be restored to allow downstream parsers/factory to read it
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void IsUblOrderDocument_ReturnsFalseForCxmlDocument()
    {
        const string cxml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML xmlns="http://www.cxml.org/cXML" payloadID="p" timestamp="2024-01-15T09:00:00Z">
              <Header/>
            </cXML>
            """;
        using var stream = ToStream(cxml);
        UblOrderParser.IsUblOrderDocument(stream).Should().BeFalse();
    }

    [Fact]
    public void IsUblOrderDocument_ReturnsFalseForBareOrderWithWrongNamespace()
    {
        const string xml = """
            <?xml version="1.0"?>
            <Order xmlns="http://example.com/not-ubl"><ID>PO-1</ID></Order>
            """;
        using var stream = ToStream(xml);
        UblOrderParser.IsUblOrderDocument(stream).Should().BeFalse();
    }

    // ── B3: locale-aware decimal parsing (shared NumberParsing reader) ───────────
    // UBL 2.1 / Peppol BIS is an invariant-locale standard ('.' decimal, ',' groups
    // thousands). The old naive ParseDecimal collapsed EU "1.234,56" to 1.23456 and let
    // "73,22" through as the catastrophic 7322. Both are now read correctly OR flagged
    // NeedsReview, and invariant numbers stay byte-identical.

    private static string UblWithQuantityAndPrice(string quantity, string price) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Order xmlns="{UblOrderNs}"
               xmlns:cbc="{CbcNs}"
               xmlns:cac="{CacNs}">
          <cbc:ID>PO-DEC</cbc:ID>
          <cbc:IssueDate>2026-05-28</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:OrderLine>
            <cac:LineItem>
              <cbc:ID>1</cbc:ID>
              <cbc:Quantity unitCode="EA">{quantity}</cbc:Quantity>
              <cac:Price>
                <cbc:PriceAmount currencyID="EUR">{price}</cbc:PriceAmount>
              </cac:Price>
              <cac:Item>
                <cbc:Name>Decimal Item</cbc:Name>
                <cac:SellersItemIdentification><cbc:ID>SUP-DEC-1</cbc:ID></cac:SellersItemIdentification>
              </cac:Item>
            </cac:LineItem>
          </cac:OrderLine>
        </Order>
        """;

    private static async Task<ParsedOrderLine> ParseUblLineAsync(string quantity, string price)
    {
        var parser = new UblOrderParser();
        var result = await parser.ParseAsync(
            ToStream(UblWithQuantityAndPrice(quantity, price)), CancellationToken.None);
        result.Lines.Should().ContainSingle();
        return result.Lines[0];
    }

    [Fact]
    public async Task ParseAsync_EuThousandsGroupedPrice_ReadsCorrectlyOrFlags()
    {
        var line = await ParseUblLineAsync(quantity: "1", price: "1.234,56");

        line.UnitPrice.Should().NotBe(1.23456m, "the thousands group must not be silently dropped");
        (line.UnitPrice == 1234.56m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_EuCommaDecimalPrice_IsNeverReadAs100x()
    {
        var line = await ParseUblLineAsync(quantity: "2", price: "73,22");

        line.UnitPrice.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.UnitPrice == 73.22m || line.NeedsReview).Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_InvariantQuantityAndPrice_AreByteUnchanged()
    {
        var line = await ParseUblLineAsync(quantity: "10", price: "73.22");

        line.Quantity.Should().Be(10m);
        line.UnitPrice.Should().Be(73.22m);
        line.NeedsReview.Should().BeFalse();

        var grouped = await ParseUblLineAsync(quantity: "1", price: "1234.56");
        grouped.UnitPrice.Should().Be(1234.56m);
        grouped.NeedsReview.Should().BeFalse();
    }
}
