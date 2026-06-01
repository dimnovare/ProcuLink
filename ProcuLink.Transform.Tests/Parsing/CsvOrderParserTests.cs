using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class CsvOrderParserTests
{
    [Fact]
    public async Task ParseAsync_CommonProcurementHeaderAliases_ReturnsParsedOrder()
    {
        var csv = """
            po_number,buyer_name,line_no,item_code,description,quantity,unit_price,currency
            DEMO-2026-001,Northwind Trading,1,ACME-WIDGET-A,Widget A 10mm,12,4.50,EUR
            DEMO-2026-001,Northwind Trading,2,ACME-WIDGET-B,Widget B 20mm,6,8.25,EUR
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("DEMO-2026-001");
        result.BuyerName.Should().Be("Northwind Trading");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(2);
        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("ACME-WIDGET-A");
        result.Lines[0].Description.Should().Be("Widget A 10mm");
        result.Lines[0].Quantity.Should().Be(12);
        result.Lines[0].UnitPrice.Should().Be(4.50m);
    }

    [Fact]
    public async Task ParseAsync_SpacedHeaders_ReturnsParsedOrder()
    {
        var csv = """
            PO Number,Buyer Name,Line No,Buyer Item Code,Description,Qty,Unit Price,Currency
            PO-42,Heinrich Industries,10,HEI-PLT-09,Industrial pallet,3,120.00,EUR
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("PO-42");
        result.BuyerName.Should().Be("Heinrich Industries");
        result.Lines.Should().ContainSingle();
        result.Lines[0].LineNumber.Should().Be(10);
        result.Lines[0].BuyerItemCode.Should().Be("HEI-PLT-09");
        result.Lines[0].Quantity.Should().Be(3);
        result.Lines[0].UnitPrice.Should().Be(120.00m);
    }
}
