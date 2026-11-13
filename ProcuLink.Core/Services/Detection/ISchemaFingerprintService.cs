namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// The hard ceiling on a heuristic supplier score. The fingerprint boost that once lived here went
/// with the format detector (plan 13b, 2026-11-13); this ceiling is what
/// <see cref="SupplierSuggestionScoring"/> still caps on.
/// </summary>
public static class FingerprintBoost
{
    /// <summary>Hard ceiling on boosted confidence — a heuristic should never claim certainty.</summary>
    public const double ConfidenceCeiling = 0.99;
}
