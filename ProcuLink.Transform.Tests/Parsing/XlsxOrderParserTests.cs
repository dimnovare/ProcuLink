using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class XlsxOrderParserTests
{
    [Fact]
    public async Task ParseAsync_HeaderRowPlusLines_ReturnsParsedOrder()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");

        var headers = new[]
        {
            "PoNumber", "BuyerName", "Currency", "OrderDate",
            "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice",
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        // Cells written as text so numeric parsing is culture-deterministic (the parser
        // reads cell.GetString() and parses with InvariantCulture).
        var line1 = new[] { "DEMO-2026-001", "Northwind Trading", "EUR", "2026-01-15", "1", "ACME-WIDGET-A", "Widget A 10mm", "12", "4.50" };
        var line2 = new[] { "DEMO-2026-001", "", "", "", "2", "ACME-WIDGET-B", "Widget B 20mm", "6", "8.25" };
        for (var c = 0; c < line1.Length; c++) ws.Cell(2, c + 1).Value = line1[c];
        for (var c = 0; c < line2.Length; c++) ws.Cell(3, c + 1).Value = line2[c];

        await using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var result = await new XlsxOrderParser().ParseAsync(stream, CancellationToken.None);

        result.PoNumber.Should().Be("DEMO-2026-001");
        result.BuyerName.Should().Be("Northwind Trading");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(2);
        result.Lines[0].LineNumber.Should().Be(1);
        result.Lines[0].BuyerItemCode.Should().Be("ACME-WIDGET-A");
        result.Lines[0].Description.Should().Be("Widget A 10mm");
        result.Lines[0].Quantity.Should().Be(12);
        result.Lines[0].UnitPrice.Should().Be(4.50m);
        result.Lines[1].BuyerItemCode.Should().Be("ACME-WIDGET-B");
        result.Lines[1].UnitPrice.Should().Be(8.25m);
    }
}
