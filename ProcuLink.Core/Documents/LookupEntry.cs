namespace ProcuLink.Core.Documents;

public sealed record LookupEntry(string From, string To);

/// <summary>The only refusals a lookup table has: it must be well formed.</summary>
public static class LookupRules
{
    public const int MaxEntries = 20_000, MaxValueLength = 200, MaxNameLength = 120;

    /// <summary>Null when the table is valid, otherwise a short reason.</summary>
    public static string? Problem(IReadOnlyList<LookupEntry> entries)
    {
        if (entries.Count > MaxEntries) return $"A lookup holds at most {MaxEntries} entries.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.From)) return "Every entry needs a value to look for.";
            if (e.From.Length > MaxValueLength || (e.To?.Length ?? 0) > MaxValueLength) return $"Values can be at most {MaxValueLength} characters.";
            if (!seen.Add(e.From.Trim())) return $"'{e.From.Trim()}' appears more than once.";
        }
        return null;
    }
}
