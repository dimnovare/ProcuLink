using ClosedXML.Excel;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

public static class XlsxGridWriter
{
    /// <summary>
    /// One worksheet, every value a TEXT cell: <c>=1+1</c> stays that literal text and never becomes a
    /// formula, and <c>1.500</c> is not coerced to a number.
    /// </summary>
    public static byte[] Write(CsvGrid grid)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");
        var r = 0;
        foreach (var row in grid.Header is null ? grid.Rows : grid.Rows.Prepend(grid.Header))
        {
            r++;
            for (var c = 0; c < row.Count; c++)
                sheet.Cell(r, c + 1).Value = row[c];
        }
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
