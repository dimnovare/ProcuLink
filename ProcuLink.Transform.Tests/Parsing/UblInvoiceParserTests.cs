using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class UblInvoiceParserTests
{
    private static readonly string MinimalUblInvoice = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                 xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                 xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
          <cbc:ID>INV-2026-001</cbc:ID>
          <cbc:IssueDate>2026-05-28</cbc:IssueDate>
          <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
          <cac:TaxTotal>
            <cbc:TaxAmount currencyID="EUR">20.00</cbc:TaxAmount>
          </cac:TaxTotal>
          <cac:LegalMonetaryTotal>
            <cbc:LineExtensionAmount currencyID="EUR">100.00</cbc:LineExtensionAmount>
            <cbc:TaxExclusiveAmount currencyID="EUR">100.00</cbc:TaxExclusiveAmount>
            <cbc:PayableAmount currencyID="EUR">120.00</cbc:PayableAmount>
          </cac:LegalMonetaryTotal>
          <cac:InvoiceLine>
            <cbc:ID>1</cbc:ID>
            <cbc:InvoicedQuantity unitCode="EA">10</cbc:InvoicedQuantity>
            <cbc:LineExtensionAmount currencyID="EUR">100.00</cbc:LineExtensionAmount>
            <cac:Item>
              <cbc:Description>Widget A</cbc:Description>
            </cac:Item>
            <cac:Price>
              <cbc:PriceAmount currencyID="EUR">10.00</cbc:PriceAmount>
            </cac:Price>
          </cac:InvoiceLine>
        </Invoice>
        """;

    private static Stream ToStream(string xml)
        => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

    [Fact]
    public async Task ParseAsync_ValidUblInvoice_ParsesHeaderCorrectly()
    {
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(MinimalUblInvoice);

        var result = await parser.ParseAsync(stream, default);

        result.InvoiceNumber.Should().Be("INV-2026-001");
        result.IssueDate.Should().Be(new DateOnly(2026, 5, 28));
        result.Currency.Should().Be("EUR");
        result.SubTotal.Should().Be(100m);
        result.TaxTotal.Should().Be(20m);
        result.GrandTotal.Should().Be(120m);
    }

    [Fact]
    public async Task ParseAsync_ValidUblInvoice_ParsesLinesCorrectly()
    {
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(MinimalUblInvoice);

        var result = await parser.ParseAsync(stream, default);

        result.Lines.Should().HaveCount(1);
        result.Lines[0].Description.Should().Be("Widget A");
        result.Lines[0].Quantity.Should().Be(10m);
        result.Lines[0].UnitCode.Should().Be("EA");
        result.Lines[0].UnitPrice.Should().Be(10m);
        result.Lines[0].LineTotal.Should().Be(100m);
    }

    [Fact]
    public async Task ParseAsync_WrongRootElement_ThrowsInvoiceParseException()
    {
        var parser = new UblInvoiceParser();
        var notInvoice = """
            <?xml version="1.0"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2">
              <ID>PO-001</ID>
            </Order>
            """;
        await using var stream = ToStream(notInvoice);

        var act = () => parser.ParseAsync(stream, default);
        await act.Should().ThrowAsync<InvoiceParseException>()
                 .WithMessage("*<Invoice>*");
    }

    [Fact]
    public void IsUblInvoiceDocument_ValidInvoice_ReturnsTrue()
    {
        using var stream = ToStream(MinimalUblInvoice);
        UblInvoiceParser.IsUblInvoiceDocument(stream).Should().BeTrue();
        stream.Position.Should().Be(0); // stream reset
    }

    [Fact]
    public void IsUblInvoiceDocument_OrderXml_ReturnsFalse()
    {
        var orderXml = """
            <?xml version="1.0"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"/>
            """;
        using var stream = ToStream(orderXml);
        UblInvoiceParser.IsUblInvoiceDocument(stream).Should().BeFalse();
    }

    [Fact]
    public void CanParse_XmlExtension_ReturnsTrue()
        => new UblInvoiceParser().CanParse(".xml").Should().BeTrue();

    [Fact]
    public void CanParse_CsvExtension_ReturnsFalse()
        => new UblInvoiceParser().CanParse(".csv").Should().BeFalse();
}
