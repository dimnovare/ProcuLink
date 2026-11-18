using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderPriceTests
{
    [Fact]
    public async Task A_sub_cent_unit_price_keeps_its_precision_and_the_line_amount_is_rounded_to_the_cent()
    {
        var read = StrictOrderFixture.Read();
        var t = read.Tables[0];
        var row = t.Rows[0];
        var cells = new Dictionary<string, ReadCell>(row.Cells)
        {
            [FieldKey.Normalise("Qty")] = row.Cells[FieldKey.Normalise("Qty")] with { Value = "10000" },
            [FieldKey.Normalise("Unit price")] = row.Cells[FieldKey.Normalise("Unit price")] with { Value = "0.0125" },
            [FieldKey.Normalise("Amount")] = row.Cells[FieldKey.Normalise("Amount")] with { Value = "125.00" },
        };
        read = new ReadResult(read.KindLabel, read.Fields, [t with { Rows = [new ReadRow(cells)] }]);

        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var ubl = XDocument.Parse(await StrictOrderFixture.Render("ubl-2-1-order", read));
        (ubl.Descendants(cbc + "PriceAmount").Single().Value, ubl.Descendants(cbc + "LineExtensionAmount").First().Value)
            .Should().Be(("0.0125", "125.00"));

        var cxml = XDocument.Parse(await StrictOrderFixture.Render("cxml-1-2", read));
        (cxml.Descendants("UnitPrice").Single().Element("Money")!.Value, cxml.Descendants("Total").Single().Element("Money")!.Value)
            .Should().Be(("0.0125", "125.00"));

        (await StrictOrderFixture.Render("x12-850", read)).Should().Contain("PO1*1*10000*PC*0.0125*");
        (await StrictOrderFixture.Render("edifact-orders", read)).Should().Contain("PRI+AAA:0.0125'");
    }
}
