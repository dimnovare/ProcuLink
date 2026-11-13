using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using System.Xml.Linq;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StandardOutputsTests
{
    private static ReadField Field(string label, string value, string? meaning) =>
        new(FieldKey.Normalise(label), label, value, "text", meaning, 1, null, false, true, false);

    private static readonly (string Label, string? Meaning)[] Columns =
    [
        ("Item code", FieldMeanings.LineItemCode), ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity),
        ("Unit", FieldMeanings.LineUnit), ("Price", FieldMeanings.LineUnitPrice), ("No", FieldMeanings.LineNumber),
    ];

    private static ReadResult Read(string poNumber = "PO-1", string qty1 = "3", string price1 = "9,99", string code2 = "B-2", string no2 = "20", string unit1 = "EA")
    {
        ReadRow Row(params string[] values) => new(Columns.Select((c, i) => (c, i))
            .ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(values[x.i], 1, null, false, false)));
        return new ReadResult("Purchase order",
            [Field("PO number", poNumber, FieldMeanings.DocumentNumber), Field("Order date", "13.11.2026", FieldMeanings.IssueDate),
             Field("Currency", "eur", FieldMeanings.Currency), Field("Buyer", "Buyer Co", FieldMeanings.SenderName),
             Field("Seller", "Seller Co", FieldMeanings.ReceiverName)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("A-1", "Bolt", qty1, unit1, price1, "10"), Row(code2, "Nut", "2", "EA", "0,50", no2)], true)]);
    }

    private static Task<DocumentRender> Render(string id, ReadResult read) =>
        StandardOutputs.RenderAsync(id, read, FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);

    private static string Text(DocumentRender r)
    {
        r.Ok.Should().BeTrue(r.Detail);
        return new StreamReader(r.Result!.Content).ReadToEnd();
    }

    [Theory]
    [InlineData("cxml-1-2")]
    [InlineData("ubl-2-1-order")]
    [InlineData("x12-850")]
    public async Task Each_standard_writes_a_conformant_file_from_meanings(string id)
    {
        var render = await Render(id, Read());
        var text = Text(render);
        var writer = StandardOutputs.Find(id)!.Writer;

        text.Should().Contain("PO-1").And.Contain("EUR");
        new ConformanceService().Check(text, writer).ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task What_a_document_lacks_is_named_and_nothing_is_written()
    {
        var render = await Render("x12-850", Read(poNumber: " ", qty1: "1.500", code2: ""));
        (render.Result is null, render.Error, render.Detail).Should()
            .Be((true, "output_invalid", "This standard needs PO number, Qty (line 1) and Item code (line 2)."));
        (await Render("cxml-1-2", Read(unit1: ""))).Detail.Should().Be("This standard needs Unit (line 1).");

        var unknown = await Render("nope", Read());
        (unknown.Error, unknown.Detail).Should().Be(("output_invalid", (string?)null));

        // An amount too large for a writer's arithmetic is a file that cannot be written, not a throw.
        var overflow = await Render("cxml-1-2", Read(qty1: "100000000000000000", price1: "1000000000000"));
        (overflow.Error, overflow.Detail).Should().Be(("output_invalid", (string?)null));
    }

    [Fact]
    public async Task Line_numbers_come_from_the_document_only_when_every_row_has_one()
    {
        Text(await Render("cxml-1-2", Read())).Should().Contain("lineNumber=\"10\"").And.Contain("lineNumber=\"20\"");
        Text(await Render("cxml-1-2", Read(no2: ""))).Should().Contain("lineNumber=\"1\"").And.Contain("lineNumber=\"2\"");
    }

    private static readonly (string Label, string? Meaning)[] InvoiceColumns =
    [
        ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity), ("Unit", FieldMeanings.LineUnit),
        ("Price", FieldMeanings.LineUnitPrice), ("Amount", FieldMeanings.LineAmount),
    ];

    // Two lines and no line tax rate: the header's 21 / 100 is the lines' rate. Endpoints are GLN-valid test ids.
    private static ReadResult InvoiceRead(string senderEndpoint = "0088:0000000000000")
    {
        ReadRow Row(params string[] values) => new(InvoiceColumns.Select((c, i) => (c, i))
            .ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(values[x.i], 1, null, false, false)));
        return new ReadResult("Invoice",
            [Field("Invoice number", "INV-1", FieldMeanings.DocumentNumber), Field("Issue date", "01.11.2026", FieldMeanings.IssueDate),
             Field("Due date", "01.12.2026", FieldMeanings.DueDate), Field("Currency", "EUR", FieldMeanings.Currency),
             Field("Seller", "Seller Co", FieldMeanings.SenderName), Field("Buyer", "Buyer Co", FieldMeanings.ReceiverName),
             Field("Sender Peppol ID", senderEndpoint, FieldMeanings.SenderEndpointId), Field("Receiver Peppol ID", "0088:0000000000017", FieldMeanings.ReceiverEndpointId),
             Field("Seller country", "LV", FieldMeanings.SenderCountry), Field("Buyer country", "LV", FieldMeanings.ReceiverCountry),
             Field("Seller VAT", "LV00000000000", FieldMeanings.SenderVatId), Field("Buyer reference", "BUYER-REF-1", FieldMeanings.BuyerReference),
             Field("Net", "100.00", FieldMeanings.TotalNet), Field("Tax", "21.00", FieldMeanings.TotalTax), Field("Gross", "121.00", FieldMeanings.TotalGross)],
            [new ReadTable("lines", "Lines", InvoiceColumns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("Bolt", "3", "C62", "20.00", "60.00"), Row("Nut", "2", "C62", "20.00", "40.00")], true)]);
    }

    [Theory]
    [InlineData("peppol-bis-billing-3", "Invoice", "InvoiceTypeCode", "380")]
    [InlineData("peppol-bis-billing-3-credit-note", "CreditNote", "CreditNoteTypeCode", "381")]
    public async Task Peppol_invoice_and_credit_note_render_from_meanings(string id, string root, string typeCode, string code)
    {
        var doc = XDocument.Parse(Text(await Render(id, InvoiceRead())));

        doc.Root!.Name.LocalName.Should().Be(root);
        doc.Descendants().Single(e => e.Name.LocalName == typeCode).Value.Should().Be(code);
        var lineRates = doc.Descendants().Where(e => e.Name.LocalName == "ClassifiedTaxCategory").Select(e => e.Elements().Single(x => x.Name.LocalName == "Percent").Value);
        lineRates.Should().Equal("21.00", "21.00");
        doc.Descendants().Count(e => e.Name.LocalName == "Country" && e.Elements().Any(x => x.Name.LocalName == "IdentificationCode")).Should().Be(2);
    }

    // The read printed negative: each listed meaning's value carries a minus sign.
    private static ReadResult Negated(ReadResult read, params string[] meanings)
    {
        string? Neg(string? meaning, string? value) => meanings.Contains(meaning) ? "-" + value : value;
        var t = read.Tables[0];
        var columns = t.Columns.ToDictionary(c => c.Key, c => c.Meaning);
        return new ReadResult(read.KindLabel, [.. read.Fields.Select(f => f with { Value = Neg(f.Meaning, f.Value) })],
            [t with { Rows = [.. t.Rows.Select(r => new ReadRow(r.Cells.ToDictionary(c => c.Key, c => c.Value with { Value = Neg(columns[c.Key], c.Value.Value) })))] }]);
    }

    // An invoice id is never flipped. Its quantities and prices stay positive, because an invoice needs them positive to be written.
    [Theory]
    [InlineData("peppol-bis-billing-3-credit-note", true, "121.00")]
    [InlineData("peppol-bis-billing-3-credit-note", false, "121.00")]
    [InlineData("en16931-cii-credit-note", true, "121.00")]
    [InlineData("peppol-bis-billing-3", false, "-121.00")]
    public async Task Credit_note_printed_negative_is_written_positive(string id, bool negativeLines, string payable)
    {
        string[] amounts = [FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross, FieldMeanings.LineAmount];
        var read = Negated(InvoiceRead(), negativeLines ? [.. amounts, FieldMeanings.LineQuantity, FieldMeanings.LineUnitPrice] : amounts);
        var doc = XDocument.Parse(Text(await Render(id, read)));
        string[] Values(params string[] names) => [.. doc.Descendants().Where(e => names.Contains(e.Name.LocalName)).Select(e => e.Value)];

        Values("PayableAmount", "DuePayableAmount").Should().Equal(payable);
        Values("CreditedQuantity", "InvoicedQuantity", "BilledQuantity")[0].Should().Be("3.0000");
        Values("PriceAmount", "ChargeAmount").Should().Equal("20.00", "20.00");
    }

    [Fact]
    public async Task A_missing_IBAN_is_named_in_capitals()
    {
        var detail = (await Render("xrechnung-ubl", InvoiceRead())).Detail;
        detail.Should().Contain("IBAN").And.NotContain("Iban");
    }

    [Fact]
    public async Task An_endpoint_without_a_scheme_is_missing()
    {
        var render = await Render("peppol-bis-billing-3", InvoiceRead(senderEndpoint: "5790000435951"));
        (render.Result is null, render.Error, render.Detail).Should().Be((true, "output_invalid", "This standard needs Sender Peppol ID."));
    }
}
