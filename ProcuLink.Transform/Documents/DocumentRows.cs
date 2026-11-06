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
    /// <paramref name="tableKey"/> is null) is that bag overlaid by its own cells. Values are placed
    /// verbatim: <c>1.500</c> and <c>1.234,50</c> go out exactly as read.
    /// </summary>
    public static DocumentRowSet Build(ReadResult read, string? tableKey)
    {
        var header = read.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .ToDictionary(f => f.Key, f => f.Value!);

        var table = tableKey is null ? read.Tables.FirstOrDefault() : read.Tables.FirstOrDefault(t => t.Key == tableKey);
        var lines = (table?.Rows ?? [])
            .Select(row =>
            {
                var bag = new Dictionary<string, string>(header);
                foreach (var (key, cell) in row.Cells)
                    if (!string.IsNullOrWhiteSpace(cell.Value)) bag[key] = cell.Value!;
                return (IReadOnlyDictionary<string, string>)bag;
            })
            .ToList();

        return new DocumentRowSet(header, lines);
    }
}
