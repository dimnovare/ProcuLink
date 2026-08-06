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

    // ════════════════════════════════════════════════════════════════════════
    // Parse failures must never become a plausible-looking value
    // ════════════════════════════════════════════════════════════════════════
    //
    // The parser used to answer "I could not read this" with today's date or 0.00 —
    // values indistinguishable from a real answer. ABSENT and UNREADABLE are different
    // facts and must not collapse into the same output.

    private static string WithIssueDate(string issueDate)
        => MinimalUblInvoice.Replace(
            "<cbc:IssueDate>2026-05-28</cbc:IssueDate>",
            $"<cbc:IssueDate>{issueDate}</cbc:IssueDate>",
            StringComparison.Ordinal);

    private static string WithPayableAmount(string amount)
        => MinimalUblInvoice.Replace(
            @"<cbc:PayableAmount currencyID=""EUR"">120.00</cbc:PayableAmount>",
            $@"<cbc:PayableAmount currencyID=""EUR"">{amount}</cbc:PayableAmount>",
            StringComparison.Ordinal);

    [Fact]
    public async Task ParseAsync_BlankAndUnparseableIssueDate_DoNotBothBecomeToday()
    {
        // THE DEFECT, VERBATIM: both a blank <IssueDate> and an unparseable one returned
        // DateOnly.FromDateTime(DateTime.UtcNow) — so an invoice with a corrupt date was
        // indistinguishable from one genuinely issued today.
        var parser = new UblInvoiceParser();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var blank = ToStream(WithIssueDate(""));
        var blankFailure = await Assert.ThrowsAsync<InvoiceParseException>(
            () => parser.ParseAsync(blank, CancellationToken.None));

        await using var garbage = ToStream(WithIssueDate("not-a-date"));
        var garbageFailure = await Assert.ThrowsAsync<InvoiceParseException>(
            () => parser.ParseAsync(garbage, CancellationToken.None));

        blankFailure.Message.Should().Contain("IssueDate");
        garbageFailure.Message.Should().Contain("IssueDate");
        garbageFailure.Message.Should().Contain("not-a-date",
            "the operator needs the token that could not be read, not just the element");
        _ = today; // referenced to document what must NOT be returned
    }

    [Fact]
    public async Task ParseAsync_ValidIssueDate_StillParses()
    {
        // DO NOT REGRESS THE WORKING CASE.
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(MinimalUblInvoice);

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.IssueDate.Should().Be(new DateOnly(2026, 5, 28));
    }

    [Fact]
    public async Task ParseAsync_UnparseableAmount_DoesNotSilentlyBecomeZero()
    {
        // A payable amount of 0.00 is a real, plausible number. Returning it because the
        // parser could not read "twelve euro" is the same class of defect as the date.
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(WithPayableAmount("twelve euro"));

        var failure = await Assert.ThrowsAsync<InvoiceParseException>(
            () => parser.ParseAsync(stream, CancellationToken.None));

        failure.Message.Should().Contain("twelve euro");
    }

    [Fact]
    public async Task ParseAsync_EuropeanDecimalAmount_IsReadNotRejected()
    {
        // The point of reading the value properly first: "1.234,56" is a well-formed
        // European amount, not garbage. It must parse — NOT become 7322-style corruption
        // and NOT hard-fail an otherwise-good invoice.
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(WithPayableAmount("1.234,56"));

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.GrandTotal.Should().Be(1234.56m);
    }

    [Fact]
    public async Task ParseAsync_EuropeanDecimalComma_IsNotReadAsHundredfold()
    {
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(WithPayableAmount("73,22"));

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.GrandTotal.Should().Be(73.22m);
        result.GrandTotal.Should().NotBe(7322m,
            "reading the decimal comma as a thousands separator is the hundredfold defect");
    }

    [Fact]
    public async Task ParseAsync_AbsentOptionalAmount_StaysZero_WithoutFailing()
    {
        // ABSENT is not UNREADABLE. An invoice that simply carries no <TaxTotal> has zero
        // tax — that is a fact, not a guess, and must not start throwing.
        var parser = new UblInvoiceParser();
        var noTax = MinimalUblInvoice.Replace(
            """
              <cac:TaxTotal>
                <cbc:TaxAmount currencyID="EUR">20.00</cbc:TaxAmount>
              </cac:TaxTotal>
            """,
            "", StringComparison.Ordinal);
        await using var stream = ToStream(noTax);

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.TaxTotal.Should().Be(0m);
        result.GrandTotal.Should().Be(120m);
    }

    [Fact]
    public async Task ParseAsync_AbsentOptionalDueDate_StaysNull_WithoutFailing()
    {
        // The fixture carries no <PaymentDueDate>. Null means "not stated" — honest.
        var parser = new UblInvoiceParser();
        await using var stream = ToStream(MinimalUblInvoice);

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.DueDate.Should().BeNull();
    }
}
