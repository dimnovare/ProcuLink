using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderItemCodeTests
{
    [Fact]
    public async Task A_read_item_code_is_the_suppliers_article_number_in_every_standard()
    {
        var x12 = (await StrictOrderFixture.Render("x12-850")).Split('~');
        x12.Where(s => s.StartsWith("PO1*")).Should().HaveCount(3)
            .And.OnlyContain(s => !s.Contains("*BP*"))
            .And.Contain(s => s.EndsWith("*PE*VP*A-100"));

        var edi = await StrictOrderFixture.Render("edifact-orders");
        edi.Should().Contain("LIN+1++A-100:SA'").And.NotContain(":IN'");

        XDocument.Parse(await StrictOrderFixture.Render("ubl-2-1-order")).Descendants()
            .Where(e => e.Name.LocalName == "BuyersItemIdentification").Should().BeEmpty();
    }
}
