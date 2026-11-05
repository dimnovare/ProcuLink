using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>The one key shape for fields, tables and columns.</summary>
public static class FieldKey
{
    public static readonly Regex Pattern = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

    /// <summary>Lower-case, non-alphanumerics to "_", repeats collapsed, trimmed; "f_" before a leading digit; "field" when empty.</summary>
    public static string Normalise(string? raw)
    {
        var key = Regex.Replace((raw ?? "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (key.Length == 0) return "field";
        if (char.IsAsciiDigit(key[0])) key = "f_" + key;
        return key.Length > 60 ? key[..60].TrimEnd('_') : key;
    }

    /// <summary>Normalises, then suffixes _2, _3, ... until the key is new to <paramref name="seen"/>, which it joins.</summary>
    public static string Unique(ISet<string> seen, string? raw)
    {
        var key = Normalise(raw);
        var candidate = key;
        for (var n = 2; !seen.Add(candidate); n++) candidate = $"{key}_{n}";
        return candidate;
    }
}

public sealed record FlowField(string Key, string Label, string Type, string? Meaning, bool Required);

public sealed record FlowTable(string Key, string Label, IReadOnlyList<FlowField> Columns);

/// <summary>Missing collections in stored JSON come back empty, never null.</summary>
public sealed record FlowFieldList(IReadOnlyList<FlowField>? Fields = null, IReadOnlyList<FlowTable>? Tables = null)
{
    public IReadOnlyList<FlowField> Fields { get; } = Fields ?? [];
    public IReadOnlyList<FlowTable> Tables { get; } = Tables ?? [];

    public static FlowFieldList Empty { get; } = new();
}
