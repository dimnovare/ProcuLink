namespace ProcuLink.Transform.Mapping;

/// <summary>A value a Lookup step looked for and did not find. Reported, never a failure.</summary>
public sealed record LookupMiss(Guid LookupId, string Value);

/// <summary>The org's lookups one render may use: per lookup id, From → To. From matches trimmed and case-insensitive.</summary>
public sealed class LookupTables(IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> byId)
{
    private readonly Dictionary<Guid, Dictionary<string, string>> _byId = byId.ToDictionary(t => t.Key,
        t => t.Value.ToDictionary(e => e.Key.Trim(), e => e.Value, StringComparer.OrdinalIgnoreCase));
    private readonly List<LookupMiss> _misses = [];
    private readonly HashSet<LookupMiss> _seen = [];

    public bool Has(Guid lookupId) => _byId.ContainsKey(lookupId);

    /// <summary>The stored To for <paramref name="value"/>; on a miss, <paramref name="mapped"/> is the value and the miss is recorded once.</summary>
    public bool TryMap(Guid lookupId, string value, out string mapped)
    {
        var key = value.Trim();
        if (_byId.TryGetValue(lookupId, out var table) && table.TryGetValue(key, out var to))
        {
            mapped = to;
            return true;
        }
        if (_seen.Add(new(lookupId, key))) _misses.Add(new(lookupId, key));
        mapped = value;
        return false;
    }

    public IReadOnlyList<LookupMiss> Misses => _misses;

    /// <summary>A fresh instance each time: it carries its own miss list.</summary>
    public static LookupTables Empty => new(new Dictionary<Guid, IReadOnlyDictionary<string, string>>());
}

/// <summary>
/// The tables in force for the current render. Set around the (synchronous) emit by the document renderer, so a
/// Lookup step can reach them without threading a parameter through the emitter the order product shares.
/// </summary>
public static class LookupScope
{
    private static readonly AsyncLocal<LookupTables?> Slot = new();

    public static LookupTables? Current => Slot.Value;

    /// <summary>Puts <paramref name="tables"/> in force until the result is disposed, which restores the previous value.</summary>
    public static IDisposable Use(LookupTables tables)
    {
        var previous = Slot.Value;
        Slot.Value = tables;
        return new Restore(previous);
    }

    private sealed class Restore(LookupTables? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
