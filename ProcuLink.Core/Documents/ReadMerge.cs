namespace ProcuLink.Core.Documents;

/// <summary>Merges the answers of a document's parts into one answer. Pure: no database, clock or I/O.</summary>
public static class ReadMerge
{
    /// <summary>The confidence a value is lowered to when the parts disagree about it.</summary>
    public const double ConflictConfidence = 0.5;

    public static ReadResult Merge(IReadOnlyList<ReadResult> parts, FlowFieldList? expected)
    {
        if (parts.Count == 0) return ReadResult.Empty;
        if (parts.Count == 1) return parts[0];

        var fields = new List<ReadField>();
        foreach (var readings in parts.SelectMany(p => p.Fields).GroupBy(f => f.Key))
        {
            var listed = expected is null || expected.Fields.Any(f => f.Key == readings.Key);
            var candidates = readings.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
            if (candidates.Count == 0)
            {
                if (expected is not null && listed) fields.Add(readings.First() with { Listed = listed });
                continue;
            }
            // OrderBy is stable, so of equal readings the earliest part stays first.
            var winner = candidates.OrderByDescending(f => f.Confidence).ThenBy(f => f.Location is null).First();
            var disagree = candidates.Any(f => !string.Equals(f.Value!.Trim(), winner.Value!.Trim(), StringComparison.Ordinal));
            fields.Add(winner with
            {
                Listed = listed,
                Confidence = disagree ? Math.Min(winner.Confidence, ConflictConfidence) : winner.Confidence,
            });
        }

        var tables = parts.SelectMany(p => p.Tables).GroupBy(t => t.Key).Select(readings => new ReadTable(
            readings.Key,
            readings.First().Label,
            readings.SelectMany(t => t.Columns).GroupBy(c => c.Key).Select(c => c.First()).ToList(),
            readings.SelectMany(t => t.Rows).ToList(),
            expected is null || expected.Tables.Any(t => t.Key == readings.Key))).ToList();

        return new ReadResult(parts.Select(p => p.KindLabel).FirstOrDefault(k => k is not null), fields, tables,
            parts.Any(p => p.Truncated));
    }

    /// <summary>A key on the flow's list reads under the flow's name for it, accents included, so a rename reaches every document read after it.</summary>
    public static ReadResult Named(ReadResult read, FlowFieldList? expected) => expected is null ? read : new(read.KindLabel,
        read.Fields.Select(f => expected.Fields.FirstOrDefault(x => x.Key == f.Key) is { } x ? f with { Label = x.Label } : f).ToList(),
        read.Tables.Select(t => expected.Tables.FirstOrDefault(x => x.Key == t.Key) is not { } x ? t : t with
        {
            Label = x.Label,
            Columns = t.Columns.Select(c => x.Columns.FirstOrDefault(y => y.Key == c.Key) is { } y ? c with { Label = y.Label } : c).ToList(),
        }).ToList(), read.Truncated);

    public static FlowFieldList KeysOf(ReadResult read) => new(
        read.Fields.Select(f => new FlowField(f.Key, f.Label, f.Type, f.Meaning, Required: false)).ToList(),
        read.Tables.Select(t => new FlowTable(t.Key, t.Label,
            t.Columns.Select(c => new FlowField(c.Key, c.Label, c.Type, c.Meaning, Required: false)).ToList())).ToList());
}
