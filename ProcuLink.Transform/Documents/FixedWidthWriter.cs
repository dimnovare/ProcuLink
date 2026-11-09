using ProcuLink.Core.Services.Mapping;
using System.Text;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>Writes the CSV grid as fixed-width records: no header row, each cell padded to its column's width.</summary>
public static class FixedWidthWriter
{
    public static byte[] Write(OutputNodeTemplate template, CsvGrid grid)
    {
        var (header, line) = OutputTemplateEmitter.CsvColumns(template);
        var leaves = header.Concat(line).ToList();
        var lineEnding = template.CsvDialect?.LineEnding ?? "\r\n";

        var sb = new StringBuilder();
        foreach (var row in grid.Rows)
        {
            for (var i = 0; i < leaves.Count; i++)
            {
                var width = leaves[i].Rule?.Width;
                if (width is not > 0)
                    throw new InvalidOperationException("Give every column a width from 1 to 1000.");
                // One record stays one line.
                var value = (i < row.Count ? row[i] : "").Replace('\r', ' ').Replace('\n', ' ');
                if (value.Length > width)
                    throw new InvalidOperationException($"\"{leaves[i].Name}\" is longer than {width} characters. Make the column wider.");
                sb.Append(value.PadRight(width.Value));
            }
            sb.Append(lineEnding);
        }
        return OutputTemplateEmitter.CsvEncoding(template.CsvDialect?.Encoding).GetBytes(sb.ToString());
    }
}
