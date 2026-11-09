using System.Text;
using ProcuLink.Core.Documents;

namespace ProcuLink.Transform.Documents;

/// <summary>Writes a <see cref="DocumentLayout"/> as plain text: title, top text, fields, table, bottom text.</summary>
public static class LayoutTextWriter
{
    public static byte[] Write(DocumentLayout layout, OutputLayout? settings)
    {
        var sections = new List<List<string>>
        {
            new[] { settings?.Title, settings?.Header }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
            layout.Fields.Where(f => !string.IsNullOrEmpty(f.Value)).Select(f => $"{f.Label}: {f.Value}").ToList(),
            Table(layout),
            string.IsNullOrEmpty(settings?.Footer) ? [] : [settings!.Footer!],
        };
        var text = string.Join("\n\n", sections.Where(s => s.Count > 0).Select(s => string.Join("\n", s)));
        return new UTF8Encoding(false).GetBytes(text.Length == 0 ? "" : text + "\n");
    }

    private static List<string> Table(DocumentLayout layout)
    {
        if (layout.Columns.Count == 0) return [];
        var widths = layout.Columns.Select((c, i) =>
            layout.Rows.Select(r => i < r.Count ? r[i].Length : 0).Append(c.Length).Max()).ToList();
        string Row(IReadOnlyList<string> cells) =>
            string.Join("  ", widths.Select((w, i) => (i < cells.Count ? cells[i] : "").PadRight(w)));
        return layout.Rows.Select(Row).Prepend(Row(layout.Columns)).ToList();
    }
}
