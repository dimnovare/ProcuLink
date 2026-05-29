namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Suggests source-column → canonical-PO-field mappings for the "magic mapping"
/// onboarding UI. Given the column headers detected in a supplier's upload file,
/// it returns the best-matching source column for each canonical field, with a
/// confidence, a short human-readable reason, and provenance.
///
/// <para>
/// The deterministic implementation is pure heuristic token matching and MUST
/// work with no AI key configured. An optional decorator can refine low-confidence
/// matches via <see cref="Ai.IAiMappingService"/> / OpenAI and re-tag provenance
/// as <c>"ai"</c>; it degrades gracefully to heuristic-only when AI is unavailable.
/// </para>
/// </summary>
public interface IFieldMappingSuggester
{
    /// <summary>
    /// Returns one suggestion per canonical PO field for which a plausible source
    /// column was found, ordered by canonical field order (header fields first,
    /// then line fields). Canonical fields with no plausible match are omitted.
    /// Each source column is assigned to at most one canonical field (best match wins).
    /// </summary>
    /// <param name="columns">
    /// Raw source column headers detected in the supplier's file. May be empty,
    /// in which case an empty list is returned.
    /// </param>
    Task<IReadOnlyList<FieldMappingSuggestion>> SuggestFieldMappingsAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string> columns,
        CancellationToken ct = default);
}

/// <summary>
/// A single canonical-field → source-column suggestion.
/// </summary>
/// <param name="CanonicalField">
/// The canonical PO field name exactly as it appears on
/// <c>ParsedOrder</c> / <c>ParsedOrderLine</c> (e.g. <c>PoNumber</c>,
/// <c>OrderDate</c>, <c>BuyerName</c>, <c>Currency</c>, <c>BuyerItemCode</c>,
/// <c>Description</c>, <c>Quantity</c>, <c>Unit</c>, <c>UnitPrice</c>,
/// <c>LineNumber</c>).
/// </param>
/// <param name="SuggestedColumn">The source column header that best matches the canonical field.</param>
/// <param name="Confidence">Match confidence in the inclusive range [0, 1].</param>
/// <param name="Reason">Short operational explanation for a procurement user.</param>
/// <param name="Source">Provenance of the suggestion: <c>"heuristic"</c> or <c>"ai"</c>.</param>
public sealed record FieldMappingSuggestion(
    string CanonicalField,
    string SuggestedColumn,
    double Confidence,
    string Reason,
    string Source);
