namespace ProcuLink.Core.Documents;

public sealed record LookupEntry(string From, string To);

/// <summary>A value a lookup may write, with an optional note that helps pick it.</summary>
public sealed record LookupCandidate(string Value, string? Description);

/// <summary>The only refusals a lookup table has: it must be well formed.</summary>
public static class LookupRules
{
    public const int MaxEntries = 20_000, MaxValueLength = 200, MaxNameLength = 120, MaxDescriptionLength = 500;

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

    /// <summary>Null when the list to choose from is valid, otherwise a short reason.</summary>
    public static string? CandidateProblem(IReadOnlyList<LookupCandidate> candidates)
    {
        if (candidates.Count > MaxEntries) return $"A lookup can list at most {MaxEntries} values to choose from.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Value)) return "Each row in the list to choose from needs a value.";
            if (c.Value.Length > MaxValueLength) return $"Values can be at most {MaxValueLength} characters.";
            if ((c.Description?.Length ?? 0) > MaxDescriptionLength) return $"Descriptions can be at most {MaxDescriptionLength} characters.";
            if (!seen.Add(c.Value.Trim())) return $"'{c.Value.Trim()}' appears more than once in the list to choose from.";
        }
        return null;
    }
}
