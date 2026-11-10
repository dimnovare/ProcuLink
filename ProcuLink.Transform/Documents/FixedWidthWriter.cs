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
        var encoding = OutputTemplateEmitter.CsvEncoding(template.CsvDialect?.Encoding);

        var sb = new StringBuilder();
        foreach (var row in grid.Rows)
        {
            for (var i = 0; i < leaves.Count; i++)
            {
                var width = leaves[i].Rule?.Width;
                if (width is not (> 0 and <= 1000))
                    throw new InvalidOperationException("Give every column a width from 1 to 1000.");
                var value = Sanitise(i < row.Count ? row[i] : "");
                // Receivers parse by byte position, so width counts encoded bytes.
                var bytes = encoding.GetByteCount(value);
                if (bytes > width)
                    throw new InvalidOperationException($"\"{leaves[i].Name}\" is longer than {width} characters. Make the column wider.");
                sb.Append(value).Append(' ', width.Value - bytes);
            }
            sb.Append(lineEnding);
        }
        return encoding.GetBytes(sb.ToString());
    }

    // One record stays one line: every control or line-separator character becomes a space.
    private static string Sanitise(string value) =>
        string.Create(value.Length, value, (span, v) =>
        {
            for (var i = 0; i < v.Length; i++)
                span[i] = char.IsControl(v[i]) || v[i] is (char)0x85 or (char)0x2028 or (char)0x2029 ? ' ' : v[i];
        });
}
