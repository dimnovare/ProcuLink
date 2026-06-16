namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// A matched org-scoped schema fingerprint — the answer to "have we seen this layout before?".
/// </summary>
/// <param name="ColumnNameHash">The canonical layout hash that matched.</param>
/// <param name="SeenCount">How many orders this org has successfully parsed with this layout.</param>
/// <param name="SampleSupplierName">Best-effort supplier name first associated with the layout. Display only.</param>
/// <param name="DetectedFormat">The format recorded for the layout (e.g. <c>"csv"</c>).</param>
/// <param name="SupplierIds">(Phase 1) The supplier(s) whose orders have used this layout. More than
/// one ⇒ a layout COLLISION: the layout is shared, so a future auto-apply must NOT silently pick one.</param>
public sealed record SchemaFingerprintMatch(
    string ColumnNameHash,
    int SeenCount,
    string? SampleSupplierName,
    string DetectedFormat,
    IReadOnlyList<Guid> SupplierIds)
{
    /// <summary>True when more than one supplier has used this exact layout (auto-apply must disambiguate).</summary>
    public bool IsSharedLayout => SupplierIds.Count > 1;

    /// <summary>Whether <paramref name="supplierId"/> is among the suppliers bound to this layout.</summary>
    public bool IsBoundTo(Guid supplierId) => SupplierIds.Contains(supplierId);
}

/// <summary>
/// Org-scoped schema fingerprinting (v1). Accumulates layouts the organisation has parsed so the
/// detector can recognise repeat layouts and surface "we've seen this N times". Strictly org-scoped:
/// no cross-org catalog (that is Horizon 3 / Group Q and explicitly out of scope here).
/// </summary>
public interface ISchemaFingerprintService
{
    /// <summary>
    /// Records a successful parse: computes the layout hash from the caller-supplied
    /// <paramref name="columnHeaders"/> and upserts the org's fingerprint (increment + last-seen).
    ///
    /// <b>Idempotent across Hangfire retries</b>: the order's hash is persisted atomically with the
    /// increment, and the method is a no-op if the order was already fingerprinted. Never throws for
    /// non-fingerprintable input (null or empty headers) — it simply does nothing.
    ///
    /// <paramref name="columnHeaders"/> and <paramref name="detectedFormat"/> are already available
    /// at the call site from <see cref="IOrderService.ParseStoredFileAsync"/> — passing them here
    /// avoids a redundant file download inside the fingerprint service.
    /// </summary>
    Task RecordParseSuccessAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<string>? columnHeaders,
        string detectedFormat,
        CancellationToken ct);

    /// <summary>
    /// Looks up an org-scoped fingerprint for the given column headers. Returns <c>null</c> when the
    /// layout has not been seen before or when there are no usable headers.
    /// </summary>
    Task<SchemaFingerprintMatch?> LookupAsync(
        Guid organisationId, IReadOnlyList<string>? columnHeaders, CancellationToken ct);
}

/// <summary>
/// Pure, side-effect-free confidence boost applied to a <see cref="DetectedFormat"/> when the
/// detected layout matches a known org fingerprint. Kept separate from the controller so the
/// boost behaviour is independently unit-testable.
/// </summary>
public static class FingerprintBoost
{
    /// <summary>Maximum additive confidence boost, regardless of how many times a layout was seen.</summary>
    public const double MaxBoost = 0.15;

    /// <summary>Additive boost per prior sighting (capped by <see cref="MaxBoost"/>).</summary>
    public const double PerSightingBoost = 0.03;

    /// <summary>Hard ceiling on boosted confidence — a heuristic should never claim certainty.</summary>
    public const double ConfidenceCeiling = 0.99;

    /// <summary>
    /// Returns <paramref name="detected"/> unchanged when <paramref name="match"/> is null or empty;
    /// otherwise returns a copy with boosted <see cref="DetectedFormat.Confidence"/>,
    /// <see cref="DetectedFormat.SeenCount"/> populated, and an explanatory reasoning line appended.
    /// </summary>
    public static DetectedFormat Apply(DetectedFormat detected, SchemaFingerprintMatch? match)
    {
        if (match is null || match.SeenCount <= 0) return detected;

        var boost = Math.Min(MaxBoost, PerSightingBoost * match.SeenCount);
        var boosted = Math.Min(ConfidenceCeiling, detected.Confidence + boost);

        var reasoning = new List<string>(detected.Reasoning)
        {
            $"Recognised column layout — your organisation has parsed this exact layout " +
            $"{match.SeenCount} time(s) before. Confidence boosted from {detected.Confidence:0.00} to {boosted:0.00}.",
        };

        return detected with { Confidence = boosted, Reasoning = reasoning, SeenCount = match.SeenCount };
    }
}
