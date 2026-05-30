namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// A matched supplier-scoped learned field mapping — the answer to "have we mapped this exact
/// layout for this supplier before, and what did the buyer→supplier item codes resolve to?".
/// </summary>
/// <param name="ColumnNameHash">The canonical layout hash that matched.</param>
/// <param name="ObservationCount">How many successful maps reinforced this mapping.</param>
/// <param name="DetectedFormat">The format recorded for the layout (e.g. <c>"csv"</c>).</param>
/// <param name="FieldMapping">
/// The learned mapping: normalised buyer item code → resolved supplier item code. Read-only.
/// </param>
public sealed record SupplierSchemaMappingMatch(
    string ColumnNameHash,
    int ObservationCount,
    string DetectedFormat,
    IReadOnlyDictionary<string, string> FieldMapping);

/// <summary>
/// The supplier-scoped field-mapping moat. Learns the buyer→supplier item-code mapping observed for
/// a supplier's CSV/XLSX <b>column layout</b> on a successful map, then returns it on a later upload
/// of the same layout for the same supplier so the parse step can pre-fill suggestions.
///
/// <para>
/// Strictly org-scoped: keyed on <c>(organisationId, supplierId, columnNameHash)</c>. The shared
/// cross-org catalog (Horizon 3 / Group Q) is explicitly out of scope. Pre-fill is advisory — it
/// must never override a deterministic item-mapping resolution or an explicit user mapping.
/// </para>
/// </summary>
public interface ISupplierSchemaMappingService
{
    /// <summary>
    /// Captures (upserts) the buyer→supplier item-code mapping learned for a supplier's column
    /// layout after a successful map. Computes the layout hash from
    /// <paramref name="columnHeaders"/> and merges <paramref name="fieldMapping"/> into the row for
    /// <c>(organisationId, supplierId, hash)</c>, incrementing the observation count.
    ///
    /// <para>
    /// <b>Idempotent and side-effect-free for empty input</b>: a null/empty header list or an empty
    /// <paramref name="fieldMapping"/> is a no-op (header-less formats have no layout to key on, and
    /// there is nothing to learn from a file with no resolved lines). Re-running it with the same
    /// mapping is safe — the merge is deterministic and the row converges.
    /// </para>
    ///
    /// <paramref name="columnHeaders"/> and <paramref name="detectedFormat"/> are already available
    /// at the call site from the parse result, so passing them here avoids re-downloading the file.
    /// </summary>
    Task CaptureAsync(
        Guid organisationId,
        Guid supplierId,
        Guid? learnedFromOrderId,
        IReadOnlyList<string>? columnHeaders,
        string detectedFormat,
        IReadOnlyDictionary<string, string> fieldMapping,
        CancellationToken ct);

    /// <summary>
    /// Reinforces the learned mapping using an already-computed <paramref name="columnNameHash"/>
    /// rather than re-deriving it from headers. Used by the Resolve flow — the strongest learning
    /// signal — where the order already carries its layout hash (persisted at parse time) but the
    /// source file's headers are no longer in hand.
    ///
    /// <para>
    /// No-op when <paramref name="columnNameHash"/> is null/blank or <paramref name="fieldMapping"/>
    /// is empty. Same idempotent merge semantics as <see cref="CaptureAsync"/>.
    /// </para>
    /// </summary>
    Task ReinforceByHashAsync(
        Guid organisationId,
        Guid supplierId,
        Guid? learnedFromOrderId,
        string? columnNameHash,
        string detectedFormat,
        IReadOnlyDictionary<string, string> fieldMapping,
        CancellationToken ct);

    /// <summary>
    /// Looks up the learned field mapping for a supplier's column layout. Returns <c>null</c> when
    /// the layout has not been mapped before for this supplier, or when there are no usable headers.
    /// The returned mapping keys are normalised buyer item codes (trimmed, lower-cased).
    /// </summary>
    Task<SupplierSchemaMappingMatch?> LookupAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string>? columnHeaders,
        CancellationToken ct);
}
