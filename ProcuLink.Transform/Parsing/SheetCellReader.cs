using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace ProcuLink.Transform.Parsing;

/// <summary>A non-empty spreadsheet cell. Row and Column are 1-based.</summary>
public sealed record SheetCell(int Row, int Column, string Text);

public static class SheetCellReader
{
    public static IReadOnlyList<SheetCell> Read(Stream content, string extension)
    {
        var cells = new List<SheetCell>();
        if (extension.TrimStart('.').Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook(content);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null) return cells;
            foreach (var cell in sheet.CellsUsed())
            {
                var text = cell.GetFormattedString();
                if (!string.IsNullOrWhiteSpace(text))
                    cells.Add(new SheetCell(cell.Address.RowNumber, cell.Address.ColumnNumber, text));
            }
        }
        else
        {
            using var reader = new StreamReader(content);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = false, DetectDelimiter = true, BadDataFound = null, MissingFieldFound = null,
            });
            var row = 0;
            while (csv.Read())
            {
                row++;
                for (var col = 0; col < csv.Parser.Count; col++)
                {
                    var text = csv.GetField(col);
                    if (!string.IsNullOrWhiteSpace(text)) cells.Add(new SheetCell(row, col + 1, text));
                }
            }
        }
        return cells;
    }

    public static string ToText(IReadOnlyList<SheetCell> cells) =>
        string.Join("\n", cells.GroupBy(c => c.Row).OrderBy(g => g.Key)
            .Select(g => string.Join(" | ", g.OrderBy(c => c.Column).Select(c => c.Text))));
}
