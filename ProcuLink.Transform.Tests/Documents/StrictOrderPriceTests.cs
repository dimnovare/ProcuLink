using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderPriceTests
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // The fixture read with its lines replaced by (qty, unit price, amount) rows built from its first line.
    private static ReadResult WithLines(params (string Qty, string Price, string Amount)[] lines)
    {
        var read = StrictOrderFixture.Read();
        var t = read.Tables[0];
        var first = t.Rows[0];
        ReadRow Row((string Qty, string Price, string Amount) l) => new(new Dictionary<string, ReadCell>(first.Cells)
        {
            [FieldKey.Normalise("Qty")] = first.Cells[FieldKey.Normalise("Qty")] with { Value = l.Qty },
            [FieldKey.Normalise("Unit price")] = first.Cells[FieldKey.Normalise("Unit price")] with { Value = l.Price },
            [FieldKey.Normalise("Amount")] = first.Cells[FieldKey.Normalise("Amount")] with { Value = l.Amount },
        });
        return new ReadResult(read.KindLabel, read.Fields, [t with { Rows = [.. lines.Select(Row)] }]);
    }

    [Fact]
    public async Task A_sub_cent_unit_price_keeps_its_precision_and_totals_sum_the_cent_rounded_line_amounts()
    {
        var read = WithLines(("10000", "0.0125", "125.00"));
        var ubl = XDocument.Parse(await StrictOrderFixture.Render("ubl-2-1-order", read));
        (ubl.Descendants(Cbc + "PriceAmount").Single().Value, ubl.Descendants(Cbc + "LineExtensionAmount").First().Value)
            .Should().Be(("0.0125", "125.00"));
        var cxml = XDocument.Parse(await StrictOrderFixture.Render("cxml-1-2", read));
        (cxml.Descendants("UnitPrice").Single().Element("Money")!.Value, cxml.Descendants("Total").Single().Element("Money")!.Value)
            .Should().Be(("0.0125", "125.00"));
        (await StrictOrderFixture.Render("x12-850", read)).Should().Contain("PO1*1*10000*PC*0.0125*");
        (await StrictOrderFixture.Render("edifact-orders", read)).Should().Contain("PRI+AAA:0.0125'");

        // 0.005 rounds to 0.01 per line: the total is 0.02, where rounding the unrounded sum would give 0.01.
        // Written "0.0050": the reader takes three digits after a dot ("0.005") as possibly a thousands group.
        var halfCents = WithLines(("1", "0.0050", "0.01"), ("1", "0.0050", "0.01"));
        XDocument.Parse(await StrictOrderFixture.Render("cxml-1-2", halfCents)).Descendants("Total").Single().Element("Money")!.Value
            .Should().Be("0.02");
        XDocument.Parse(await StrictOrderFixture.Render("ubl-2-1-order", halfCents)).Descendants().Single(e => e.Name.LocalName == "AnticipatedMonetaryTotal")
            .Element(Cbc + "LineExtensionAmount")!.Value.Should().Be("0.02");
    }
}
