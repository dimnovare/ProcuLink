using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Documents;

public class SheetCellReaderTests
{
    [Fact]
    public void Sparse_xlsx_row_and_semicolon_csv_keep_columns()
    {
        using var xlsx = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(2, 1).Value = "Bolt";
            ws.Cell(2, 2).Value = 10;
            ws.Cell(2, 4).Value = 2.5;
            wb.SaveAs(xlsx);
        }
        xlsx.Position = 0;
        var sheet = SheetCellReader.Read(xlsx, ".xlsx");

        SheetCellReader.ToText(sheet).Should().Be("Bolt | 10 |  | 2.5");
        ValueLocator.LocateInCells("2.5", sheet)!.Address.Should().Be("cell:r2c4");

        var csv = SheetCellReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("Item;Qty;Discount;Price\nBolt;10;;2,50\n")), ".csv");
        SheetCellReader.ToText(csv).Should().Be("Item | Qty | Discount | Price\nBolt | 10 |  | 2,50");
        ValueLocator.LocateInCells("2,50", csv)!.Address.Should().Be("cell:r2c4");
    }
}
