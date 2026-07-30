namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// The ONE case policy for matching a line's buyer item code against a learned
/// <c>ItemMapping</c> row — live (<c>ItemMappingService.ResolveAsync</c> /
/// <c>ResolveManyAsync</c>) and pinned (<c>OrderIngestionService.ResolveFromSnapshot</c>) alike.
///
/// <para><b>Why this exists (WP-14 part 3).</b> A line's supplier code is resolved by two adjacent
/// mechanisms: the learned item mappings, and the supplier CATALOG. The catalog folds case — its
/// lookup dictionary is keyed <see cref="StringComparer.OrdinalIgnoreCase"/> and its exact pass
/// compares <see cref="StringComparison.OrdinalIgnoreCase"/>. The learned mappings did not: they
/// compared with <c>==</c>, so <c>"b-1"</c> and <c>"B-1"</c> were different codes. A buyer whose
/// ERP exported the same SKU in a different case on a later PO therefore lost every learned mapping
/// and dropped every line to manual review — while the very same difference in case resolved fine
/// on the catalog path. Whether a code resolves must not depend on which resolver saw it.</para>
///
/// <para><b>The policy.</b> Trim, then match case-INSENSITIVELY, but prefer an ORDINAL-exact row
/// when one exists. Preferring the exact row makes the change strictly additive: every code that
/// resolved before still resolves to exactly the same supplier code, and folding case only turns
/// former <c>null</c>s into hits. An org that deliberately keeps <c>"abc"</c> and <c>"ABC"</c> as
/// distinct mappings keeps both. When several rows differ ONLY in case and none matches exactly,
/// the winner is the last in ordinal order — arbitrary, but deterministic, so a re-run, a replay
/// and a pinned revision all agree.</para>
/// </summary>
public static class BuyerItemCodeMatch
{
    /// <summary>The comparer defining "the same buyer item code" for candidate grouping.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// True when <paramref name="stored"/> is a candidate row for <paramref name="requested"/>.
    /// Both sides are trimmed; blank never matches.
    /// </summary>
    public static bool Matches(string? stored, string? requested)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(requested)) return false;
        return string.Equals(stored.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pick the winning row for <paramref name="requested"/> out of <paramref name="candidates"/>
    /// (rows already known to match case-insensitively). An ordinal-exact row wins; otherwise the
    /// last candidate in ordinal order of its stored code wins, so the choice is deterministic
    /// rather than dependent on DB row order.
    /// </summary>
    /// <param name="candidates">(storedCode, value) pairs, in the order the source produced them.</param>
    /// <param name="requested">The code as the document printed it (trimmed by the caller).</param>
    public static TValue? Pick<TValue>(
        IReadOnlyList<(string StoredCode, TValue Value)> candidates, string requested)
        where TValue : class
    {
        if (candidates.Count == 0) return null;

        // Exact (ordinal) wins — the pre-WP-14 behaviour, preserved byte-for-byte. "Last exact wins"
        // mirrors the old row loop, which overwrote the key as it walked the rows.
        TValue? exact = null;
        foreach (var (stored, value) in candidates)
            if (string.Equals(stored.Trim(), requested, StringComparison.Ordinal))
                exact = value;
        if (exact is not null) return exact;

        // No exact row — fall back to the case-differing rows, deterministically.
        return candidates
            .OrderBy(c => c.StoredCode, StringComparer.Ordinal)
            .Select(c => c.Value)
            .LastOrDefault();
    }
}
