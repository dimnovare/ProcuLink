using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output.Ksef;
using Xunit;

namespace ProcuLink.Transform.Tests.Output.Ksef;

/// <summary>Synthetic parties only: NIPs 1111111111 / 9999999999 pass the checksum and belong to no one.</summary>
public class Fa3InvoiceWriterTests
{
    private static readonly XNamespace Ns = Fa3Schema.Namespace;

    private static readonly (string Label, string Meaning)[] Columns =
    [
        ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity), ("Price", FieldMeanings.LineUnitPrice),
        ("Amount", FieldMeanings.LineAmount), ("VAT rate", FieldMeanings.LineTaxRate),
    ];

    /// <summary>
    /// Two lines at 23 % (2 × 50.00, 1 × 100.00) and one at 8 % (1 × 50.00): net 250.00, VAT 46.00 + 4.00, gross 300.00.
    /// <paramref name="set"/> overrides a header field by label, or a cell as "line{n}.{column}".
    /// </summary>
    public static ReadResult Fixture(params (string Key, string Value)[] set)
    {
        var o = set.ToDictionary(x => x.Key, x => x.Value);
        string V(string key, string value) => o.GetValueOrDefault(key, value);
        ReadField F(string label, string value, string meaning) =>
            new(FieldKey.Normalise(label), label, V(label, value), "text", meaning, 1, null, false, true, false);
        string[][] rows = [["Usługa A", "2", "50.00", "100.00", "23"], ["Usługa B", "1", "100.00", "100.00", "23"], ["Towar C", "1", "50.00", "50.00", "8"]];
        return new ReadResult("Invoice",
            [F("Invoice number", "FV/2026/11/0001", FieldMeanings.DocumentNumber), F("Issue date", "20.11.2026", FieldMeanings.IssueDate),
             F("Currency", "PLN", FieldMeanings.Currency),
             F("Seller", "Przykładowa Spółka Testowa sp. z o.o.", FieldMeanings.SenderName), F("Seller NIP", "1111111111", FieldMeanings.SenderVatId),
             F("Seller country", "PL", FieldMeanings.SenderCountry), F("Seller street", "ul. Testowa 1", FieldMeanings.SenderStreet),
             F("Seller postcode", "00-001", FieldMeanings.SenderPostcode), F("Seller city", "Warszawa", FieldMeanings.SenderCity),
             F("Buyer", "Fikcyjny Odbiorca S.A.", FieldMeanings.ReceiverName), F("Buyer NIP", "9999999999", FieldMeanings.ReceiverVatId),
             F("Net", "250.00", FieldMeanings.TotalNet), F("VAT", "50.00", FieldMeanings.TotalTax), F("Gross", "300.00", FieldMeanings.TotalGross)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [.. rows.Select((r, n) => new ReadRow(Columns.Select((c, i) => (c, i)).ToDictionary(x => FieldKey.Normalise(x.c.Label),
                    x => new ReadCell(V($"line{n + 1}.{x.c.Label}", r[x.i]), 1, null, false, false))))], true)]);
    }

    public static Task<DocumentRender> Render(ReadResult read) =>
        StandardOutputs.RenderAsync("ksef-fa-3", read, FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);

    private static byte[] Bytes(DocumentRender r)
    {
        r.Error.Should().BeNull(r.Detail);
        using var ms = new MemoryStream();
        r.Result!.Content.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task KSeF_file_is_made_from_meanings_and_passes_the_official_schema()
    {
        var bytes = Bytes(await Render(Fixture()));
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "KSeF wants UTF-8 without a BOM");
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        Fa3Schema.FirstError(xml).Should().BeNull();
        string V(string name) => xml.Descendants(Ns + name).First().Value;

        (V("P_13_1"), V("P_14_1"), V("P_13_2"), V("P_14_2"), V("P_15")).Should().Be(("200.00", "46.00", "50.00", "4.00", "300.00"));
        xml.Root!.Element(Ns + "Podmiot1")!.Descendants(Ns + "NIP").Single().Value.Should().Be("1111111111");
        xml.Root!.Element(Ns + "Podmiot2")!.Descendants(Ns + "NIP").Single().Value.Should().Be("9999999999");
        (V("JST"), V("GV"), V("RodzajFaktury")).Should().Be(("2", "2", "VAT"));
        xml.Descendants(Ns + "FaWiersz").Should().HaveCount(3);
        V("P_12").Should().Be("23");

        var summary = Fa3Summary.Read(bytes);
        summary.Rates.Select(r => r.Rate).Should().Equal("23", "8");
        (summary.SellerNip, summary.Number, summary.Gross, summary.Lines).Should().Be(("1111111111", "FV/2026/11/0001", "300.00", 3));
        // Stable across renders: only DataWytworzeniaFa differs between two, and the hash leaves it out; any other change moves it.
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var created = V("DataWytworzeniaFa");
        Fa3Summary.ConfirmHash(System.Text.Encoding.UTF8.GetBytes(text.Replace(created, "2030-01-01T00:00:00Z"))).Should().Be(summary.Confirm).And.MatchRegex("^[0-9a-f]{64}$");
        Fa3Summary.ConfirmHash(System.Text.Encoding.UTF8.GetBytes(text.Replace("Usługa A", "Usługa X"))).Should().NotBe(summary.Confirm);
    }

    // Live, an AI read wrote the seller's country "Polska", which reached the writer as POLSKA and was refused. A Greek buyer's
    // VAT number written with the ISO code GR goes out with FA(3)'s EU prefix EL.
    [Fact]
    public async Task KSeF_file_is_made_when_the_country_is_written_as_a_name()
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(Bytes(await Render(Fixture(("Seller country", "Polska"), ("Buyer NIP", "GR000000000"))))));
        var buyer = xml.Root!.Element(Ns + "Podmiot2")!;
        (buyer.Descendants(Ns + "KodUE").Single().Value, buyer.Descendants(Ns + "NrVatUE").Single().Value).Should().Be(("EL", "000000000"));
    }

    [Theory]
    [InlineData("Seller NIP", "1111111112", "The seller's tax number 1111111112 isn't a valid Polish NIP. Check the seller's NIP on the document.")]
    [InlineData("Currency", "EUR", "The KSeF invoice file is made for invoices in PLN for now. This document is in EUR.")]
    [InlineData("line3.VAT rate", "19", "The KSeF invoice file supports VAT rates of 23%, 8% and 5% for now. Line 3 has 19%.")]
    [InlineData("line1.Price|line1.Amount", "61.50|123.00",
        "The line amounts add up to 273.00, but the document's net total is 250.00. Check the lines and totals before making the KSeF file.")]
    [InlineData("line1.Amount", "101.00", "Line 1: 2 × 50.00 is 100.00, but the line amount is 101.00. Check the line before making the KSeF file.")]
    [InlineData("VAT", "51.00", "The VAT worked out from the lines is 50.00, but the document's VAT total is 51.00.")]
    // No line has a readable rate, and the totals blend to exactly 8 %: never issued as P_12=8 from the header.
    [InlineData("line1.VAT rate|line2.VAT rate|line3.VAT rate|VAT|Gross", "zw|zw|zw|20.00|270.00",
        "This standard needs VAT rate (line 1).")]
    public async Task KSeF_file_refuses_what_it_cannot_prove(string keys, string values, string expectedDetail)
    {
        var render = await Render(Fixture([.. keys.Split('|').Zip(values.Split('|'))]));
        (render.Error, render.Detail, render.Result is null).Should().Be(("output_invalid", expectedDetail, true));
    }

    // Our arithmetic rounds 1 × 50.000000001 to the line's 50.00, so only the official schema (P_9A allows 8 decimals) refuses it.
    // Mutation-checked: with Fa3Schema.FirstError returning null, this test (and only this one) goes red.
    [Fact]
    public async Task Schema_gate_refuses_what_the_checks_let_through()
    {
        var render = await Render(Fixture(("line3.Price", "50.000000001")));
        (render.Error, render.Detail).Should().Be(("output_invalid",
            "This file didn't pass the Ministry of Finance FA(3) schema check (at P_9A). Nothing was made or sent."));
    }
}
