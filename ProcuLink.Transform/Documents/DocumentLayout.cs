using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>A CSV grid split back into labelled fields and a table, for the pdf and text outputs.</summary>
public sealed record DocumentLayout(
    IReadOnlyList<(string Label, string Value)> Fields,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public static DocumentLayout From(OutputNodeTemplate template, CsvGrid grid, bool hasTableRows)
    {
        // Labels come from the nodes: grid.Header is null when the header row is switched off.
        var (header, line) = OutputTemplateEmitter.CsvColumns(template);
        var first = grid.Rows.Count > 0 ? grid.Rows[0] : [];
        var fields = header.Select((n, i) => (n.Name, i < first.Count ? first[i] : "")).ToList();
        if (!hasTableRows) return new(fields, [], []);
        return new(fields, line.Select(n => n.Name).ToList(),
            grid.Rows.Select(r => (IReadOnlyList<string>)r.Skip(header.Count).ToList()).ToList());
    }
}
