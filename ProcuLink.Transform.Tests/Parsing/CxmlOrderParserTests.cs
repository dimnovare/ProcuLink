using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class CxmlOrderParserTests
{
    private static string SampleCxml(
        string orderID      = "PO-12345",
        string orderDate    = "2024-01-15",
        string currency     = "EUR",
        string deployMode   = "production",
        string suppPartId   = "SUP-ABC-001",
        string quantity     = "10",
        string unitPrice    = "125.00",
        string description  = "Widget Type A",
        string unit         = "EA",
        bool   includeItem  = true) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <cXML payloadID="test@proculink" timestamp="2024-01-15T09:00:00Z" xml:lang="en-US">
          <Header>
            <From>
              <Credential domain="DUNS">
                <Identity>buyer-id</Identity>
              </Credential>
            </From>
            <To>
              <Credential domain="DUNS">
                <Identity>supplier-id</Identity>
              </Credential>
            </To>
            <Sender>
              <Credential domain="NetworkUserId">
                <Identity>sender</Identity>
              </Credential>
            </Sender>
          </Header>
          <Request deploymentMode="{deployMode}">
            <OrderRequest>
              <OrderRequestHeader orderID="{orderID}" orderDate="{orderDate}" type="new">
                <Total>
                  <Money currency="{currency}">1250.00</Money>
                </Total>
              </OrderRequestHeader>
              {(includeItem ? $"""
              <ItemOut quantity="{quantity}" lineNumber="1">
                <ItemID>
                  <SupplierPartID>{suppPartId}</SupplierPartID>
                </ItemID>
                <ItemDetail>
                  <UnitPrice>
                    <Money currency="{currency}">{unitPrice}</Money>
                  </UnitPrice>
                  <Description xml:lang="en">{description}</Description>
                  <UnitOfMeasure>{unit}</UnitOfMeasure>
                </ItemDetail>
              </ItemOut>
              """ : "")}
            </OrderRequest>
          </Request>
        </cXML>
        """;

    private static Stream ToStream(string xml) =>
        new MemoryStream(Encoding.UTF8.GetBytes(xml));

    // ── CanParse ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrueForCxmlAndXml()
    {
        var parser = new CxmlOrderParser();
        parser.CanParse(".cxml").Should().BeTrue();
        parser.CanParse(".CXML").Should().BeTrue();
        parser.CanParse(".xml").Should().BeTrue();
        parser.CanParse(".XML").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".pdf").Should().BeFalse();
    }

    // ── Real-world cXML (Coupa / Nasdaq) — regression for the live failures ──────

    [Fact]
    public async Task ParseAsync_RealCoupaCxml_WithDoctypeAndNoDeploymentMode_ParsesAndKeepsRealCodes()
    {
        // A genuine Coupa OrderRequest (Nasdaq → Markit) that failed on prod 2026-06-14:
        // it carries a <!DOCTYPE cXML SYSTEM "…dtd"> header and its <Request> has NO
        // deploymentMode attribute. Both used to throw. It also carries the real codes
        // (SupplierPartID 39424093, ManufacturerPartID REDACTED-ORDER-DATA) that must survive.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cxml-coupa-orderrequest-sek.cxml");
        await using var stream = File.OpenRead(path);

        var result = await new CxmlOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("152400");
        result.Currency.Should().Be("SEK");
        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.BuyerItemCode.Should().Be("39424093");                 // SupplierPartID
        line.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");        // real Apple MPN — was dropped
        line.CustomerPartNumber.Should().Be("180743323");           // SupplierPartAuxiliaryID
        line.Quantity.Should().Be(1m);
        line.UnitPrice.Should().Be(3179.15m);
        line.Unit.Should().Be("EA");
        line.Description.Should().Contain("Apple Magic Keyboard");
        line.Unspsc.Should().BeNull();                              // "unknown" placeholder → null
    }

    [Fact]
    public async Task ParseAsync_WithDoctypeDeclaration_DoesNotThrow()
    {
        // DtdProcessing.Prohibit (the XDocument default) threw "DTD is prohibited" on every
        // real cXML, all of which carry a DOCTYPE. We now read with DtdProcessing.Ignore.
        var withDoctype = SampleCxml(orderID: "PO-DTD-1").Replace(
            "<cXML ",
            "<!DOCTYPE cXML SYSTEM \"http://xml.cxml.org/schemas/cXML/1.2.014/cXML.dtd\">\n<cXML ");

        var result = await new CxmlOrderParser().ParseAsync(ToStream(withDoctype), CancellationToken.None);

        result.PoNumber.Should().Be("PO-DTD-1");
    }

    [Fact]
    public async Task ParseAsync_RequestWithoutDeploymentMode_DefaultsToProductionAndParses()
    {
        var noDeployMode = SampleCxml(orderID: "PO-NODEP-1")
            .Replace("<Request deploymentMode=\"production\">", "<Request>");

        var result = await new CxmlOrderParser().ParseAsync(ToStream(noDeployMode), CancellationToken.None);

        result.PoNumber.Should().Be("PO-NODEP-1");
        result.Lines.Should().HaveCount(1);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_ValidCxml_MapsAllHeaderFields()
    {
        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(
            ToStream(SampleCxml(orderID: "PO-12345", orderDate: "2024-01-15", currency: "EUR")),
            CancellationToken.None);

        result.PoNumber.Should().Be("PO-12345");
        result.OrderDate.Should().Be(new DateTime(2024, 1, 15));
        result.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task ParseAsync_ValidCxml_MapsLineFields()
    {
        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(
            ToStream(SampleCxml(suppPartId: "SUP-ABC-001", quantity: "10", unitPrice: "125.00",
                                description: "Widget Type A", unit: "EA")),
            CancellationToken.None);

        result.Lines.Should().HaveCount(1);
        var line = result.Lines[0];
        line.LineNumber.Should().Be(1);
        line.BuyerItemCode.Should().Be("SUP-ABC-001");
        line.Quantity.Should().Be(10m);
        line.UnitPrice.Should().Be(125.00m);
        line.Description.Should().Be("Widget Type A");
        line.Unit.Should().Be("EA");
    }

    [Fact]
    public async Task ParseAsync_CurrencyExtractedFromMoney_IsUppercase()
    {
        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(
            ToStream(SampleCxml(currency: "usd")),
            CancellationToken.None);

        result.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task ParseAsync_TwoItemOuts_ParsesBothLines()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML payloadID="test@proculink" timestamp="2024-01-15T09:00:00Z" xml:lang="en-US">
              <Header>
                <From><Credential domain="DUNS"><Identity>buyer</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>supplier</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-MULTI" orderDate="2024-06-01" type="new">
                    <Total><Money currency="EUR">500.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="10" lineNumber="1">
                    <ItemID><SupplierPartID>PART-A</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">25.00</Money></UnitPrice>
                      <Description xml:lang="en">Item A</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                  <ItemOut quantity="5" lineNumber="2">
                    <ItemID><SupplierPartID>PART-B</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">50.00</Money></UnitPrice>
                      <Description xml:lang="en">Item B</Description>
                      <UnitOfMeasure>KG</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.Lines.Should().HaveCount(2);
        result.Lines[0].BuyerItemCode.Should().Be("PART-A");
        result.Lines[0].Quantity.Should().Be(10m);
        result.Lines[1].BuyerItemCode.Should().Be("PART-B");
        result.Lines[1].Quantity.Should().Be(5m);
        result.Lines[1].Unit.Should().Be("KG");
    }

    // ── Namespace variant ─────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_NamespacedCxml_ParsesCorrectly()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML xmlns="http://www.cxml.org/cXML"
                  payloadID="ns-test@proculink" timestamp="2024-03-10T10:00:00Z" xml:lang="en-US">
              <Header>
                <From><Credential domain="DUNS"><Identity>buyer-ns</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>supplier-ns</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request deploymentMode="test">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-NS-001" orderDate="2024-03-10" type="new">
                    <Total><Money currency="GBP">99.99</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="3" lineNumber="1">
                    <ItemID><SupplierPartID>NS-PART-X</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="GBP">33.33</Money></UnitPrice>
                      <Description xml:lang="en">Namespaced Item</Description>
                      <UnitOfMeasure>M</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.PoNumber.Should().Be("PO-NS-001");
        result.Currency.Should().Be("GBP");
        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("NS-PART-X");
        result.Lines[0].Quantity.Should().Be(3m);
    }

    // ── Missing required fields ───────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_MissingOrderID_ThrowsCxmlParseException()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML payloadID="err@proculink" timestamp="2024-01-15T09:00:00Z">
              <Header>
                <From><Credential domain="DUNS"><Identity>b</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>s</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderDate="2024-01-15" type="new">
                    <Total><Money currency="EUR">100.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="1" lineNumber="1">
                    <ItemID><SupplierPartID>PART-X</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">100.00</Money></UnitPrice>
                      <Description xml:lang="en">Item</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var parser = new CxmlOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>()
                 .WithMessage("*orderID*");
    }

    [Fact]
    public async Task ParseAsync_MissingDeploymentMode_DefaultsToProductionAndParses()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML payloadID="err@proculink" timestamp="2024-01-15T09:00:00Z">
              <Header>
                <From><Credential domain="DUNS"><Identity>b</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>s</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request>
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-123" orderDate="2024-01-15" type="new">
                    <Total><Money currency="EUR">100.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="1" lineNumber="1">
                    <ItemID><SupplierPartID>PART-X</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">100.00</Money></UnitPrice>
                      <Description xml:lang="en">Item</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        // deploymentMode is optional in cXML (defaults to "production"); Coupa omits it.
        var parser = new CxmlOrderParser();
        var result = await parser.ParseAsync(ToStream(xml), CancellationToken.None);

        result.PoNumber.Should().Be("PO-123");
        result.Lines.Should().HaveCount(1);
    }

    [Fact]
    public async Task ParseAsync_NoItemOut_ThrowsCxmlParseException()
    {
        var parser = new CxmlOrderParser();
        var act = async () => await parser.ParseAsync(
            ToStream(SampleCxml(includeItem: false)),
            CancellationToken.None);

        await act.Should().ThrowAsync<CxmlParseException>()
                 .WithMessage("*ItemOut*");
    }

    [Fact]
    public async Task ParseAsync_MissingSupplierPartID_ThrowsCxmlParseException()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML payloadID="err@proculink" timestamp="2024-01-15T09:00:00Z">
              <Header>
                <From><Credential domain="DUNS"><Identity>b</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>s</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-NOPART" orderDate="2024-01-15" type="new">
                    <Total><Money currency="EUR">100.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="1" lineNumber="1">
                    <ItemID/>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">100.00</Money></UnitPrice>
                      <Description xml:lang="en">Item</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var parser = new CxmlOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>()
                 .WithMessage("*SupplierPartID*");
    }

    [Fact]
    public async Task ParseAsync_MissingUnitPriceMoney_ThrowsCxmlParseException()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML payloadID="err@proculink" timestamp="2024-01-15T09:00:00Z">
              <Header>
                <From><Credential domain="DUNS"><Identity>b</Identity></Credential></From>
                <To><Credential domain="DUNS"><Identity>s</Identity></Credential></To>
                <Sender><Credential domain="NetworkUserId"><Identity>s</Identity></Credential></Sender>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-NOPRICE" orderDate="2024-01-15" type="new">
                    <Total><Money currency="EUR">100.00</Money></Total>
                  </OrderRequestHeader>
                  <ItemOut quantity="1" lineNumber="1">
                    <ItemID><SupplierPartID>PART-X</SupplierPartID></ItemID>
                    <ItemDetail>
                      <Description xml:lang="en">Item</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """;

        var parser = new CxmlOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>()
                 .WithMessage("*UnitPrice*");
    }

    [Fact]
    public async Task ParseAsync_NotCxml_ThrowsCxmlParseException()
    {
        const string xml = """
            <?xml version="1.0"?>
            <PurchaseOrder><Header/></PurchaseOrder>
            """;

        var parser = new CxmlOrderParser();
        var act = async () => await parser.ParseAsync(ToStream(xml), CancellationToken.None);
        await act.Should().ThrowAsync<CxmlParseException>()
                 .WithMessage("*cXML*");
    }

    // ── Fixture file test ─────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_SampleFixtureFile_ParsesCorrectly()
    {
        var fixturePath = Path.Combine(
            Path.GetDirectoryName(typeof(CxmlOrderParserTests).Assembly.Location)!,
            "Fixtures", "sample-order.cxml");

        // Skip if fixture not deployed (e.g. in some CI configurations)
        if (!File.Exists(fixturePath))
            return;

        var parser = new CxmlOrderParser();
        await using var stream = File.OpenRead(fixturePath);
        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-12345");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(2);
        result.Lines[0].BuyerItemCode.Should().Be("SUP-ABC-001");
        result.Lines[0].Quantity.Should().Be(10m);
        result.Lines[1].BuyerItemCode.Should().Be("SUP-XYZ-002");
        result.Lines[1].Quantity.Should().Be(5m);
    }
}
