using System.Globalization;
using System.Text;
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
            using var workbook = XlsxCompressionFallback.OpenWorkbook(content);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null) return cells;
            foreach (var cell in sheet.CellsUsed())
            {
                var text = XlsxTextExtractor.FormatCell(cell);
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

    /// <summary>
    /// One line per row, a blank cell keeping its column ("Bolt | 10 |  | 2.50"). Stops after the row that takes the
    /// text past <paramref name="maxChars"/>, so a far-right cell on many rows cannot balloon memory.
    /// </summary>
    public static string ToText(IReadOnlyList<SheetCell> cells, int maxChars = int.MaxValue)
    {
        var sb = new StringBuilder();
        foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            var byColumn = row.ToDictionary(c => c.Column, c => c.Text);
            if (sb.Length > 0) sb.Append('\n');
            sb.AppendJoin(" | ", Enumerable.Range(1, byColumn.Keys.Max()).Select(i => byColumn.GetValueOrDefault(i, "")));
            if (sb.Length > maxChars) break;
        }
        return sb.ToString();
    }
}
