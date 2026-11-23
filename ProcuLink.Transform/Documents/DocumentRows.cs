using ProcuLink.Core.Documents;

namespace ProcuLink.Transform.Documents;

/// <summary>The value bags an output template reads: one for the header, one per table row.</summary>
public sealed record DocumentRowSet(
    IReadOnlyDictionary<string, string> Header,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Lines);

public static class DocumentRows
{
    /// <summary>
    /// Fields with a value become the header bag; each row of the chosen table (the first when
    /// <paramref name="tableKey"/> is null) is that bag overlaid by its own cells (a present-but-blank cell blanks the key rather than inheriting the header).
    /// Without <paramref name="decimalMark"/> values are placed verbatim. With it, a certain date becomes yyyy-MM-dd and a certain amount bare digits
    /// with that mark, so every file of a flow writes them one way; an uncertain one (<c>1.500</c>, <c>03/04/2026</c>, <c>EUR 12</c>) still goes out as read.
    /// </summary>
    public static DocumentRowSet Build(ReadResult read, string? tableKey, char? decimalMark = null)
    {
        string Value(string type, string v) => decimalMark is not { } mark || string.IsNullOrWhiteSpace(v) ? v : type switch
        {
            FieldTypes.Date => FieldValueParser.IsoDate(v) ?? v,
            FieldTypes.Money or FieldTypes.Number => FieldValueParser.PlainNumber(v, mark) ?? v,
            _ => v,
        };

        // A repeated key keeps its first non-blank value; ReadResult does not guarantee unique keys.
        var header = new Dictionary<string, string>();
        foreach (var f in read.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
            header.TryAdd(f.Key, Value(f.Type, f.Value!));

        var table = tableKey is null ? read.Tables.FirstOrDefault() : read.Tables.FirstOrDefault(t => t.Key == tableKey);
        var types = (table?.Columns ?? []).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Type);
        var lines = (table?.Rows ?? [])
            .Select(row =>
            {
                var bag = new Dictionary<string, string>(header);
                foreach (var (key, cell) in row.Cells)
                    bag[key] = Value(types.GetValueOrDefault(key, FieldTypes.Text), cell.Value ?? "");
                return (IReadOnlyDictionary<string, string>)bag;
            })
            .ToList();

        return new DocumentRowSet(header, lines);
    }
}
