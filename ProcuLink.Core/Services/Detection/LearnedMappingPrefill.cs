using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Pure, side-effect-free policy for turning a supplier's learned schema mapping into a line-level
/// suggestion. Kept separate from <c>OrderService</c> so the moat's pre-fill rule is independently
/// unit-testable without a database or the parse pipeline.
///
/// <para>
/// Invariants enforced here:
/// <list type="bullet">
///   <item>It only ever produces a <see cref="AiMappingSuggestion"/> — never a hard resolution.
///         The caller decides whether a deterministic mapping already won (in which case it never
///         calls this).</item>
///   <item>Buyer codes are matched case-insensitively / whitespace-tolerantly, agreeing with how
///         <see cref="ISupplierSchemaMappingService"/> normalises keys on capture.</item>
///   <item>Provenance is tagged <see cref="Provenance"/> so the Resolve UI can distinguish a
///         learned-mapping pre-fill from an AI suggestion.</item>
/// </list>
/// </para>
/// </summary>
public static class LearnedMappingPrefill
{
    /// <summary>Provenance label stamped on suggestions sourced from a learned schema mapping.</summary>
    public const string Provenance = "learned-schema-mapping";

    /// <summary>
    /// High but deliberately sub-certain confidence (0.9): a confirmed prior mapping for this exact
    /// supplier + layout, yet still surfaced as a suggestion the user can override — never auto-applied.
    /// </summary>
    public const float Confidence = 0.9f;

    /// <summary>
    /// Returns a suggestion sourced from <paramref name="learnedMapping"/> when it already knows
    /// <paramref name="buyerItemCode"/>, otherwise <c>null</c>. A null/empty mapping or a blank buyer
    /// code yields <c>null</c>.
    /// </summary>
    public static AiMappingSuggestion? TryBuild(
        string buyerItemCode, IReadOnlyDictionary<string, string>? learnedMapping)
    {
        if (learnedMapping is null || learnedMapping.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(buyerItemCode)) return null;

        var key = buyerItemCode.Trim().ToLowerInvariant();
        if (!learnedMapping.TryGetValue(key, out var supplierCode) || string.IsNullOrWhiteSpace(supplierCode))
            return null;

        return new AiMappingSuggestion(
            SupplierItemCode: supplierCode,
            Confidence: Confidence,
            Reason: "Your organisation mapped this buyer code on a previous order with the same column layout for this supplier.",
            Provenance: Provenance);
    }
}
