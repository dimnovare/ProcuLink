namespace ProcuLink.Core.Services.Ai;

public interface IAiMappingService
{
    Task<AiMappingSuggestion?> SuggestSupplierItemCodeAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        AiMappingLineContext line,
        IReadOnlyList<AiMappingCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>
    /// Batch variant of <see cref="SuggestSupplierItemCodeAsync"/>: suggests supplier
    /// item codes for many unresolved lines in a single structured-output request,
    /// instead of one network round-trip per line.
    /// </summary>
    /// <returns>
    /// A dictionary keyed by <see cref="AiMappingLineContext.LineNumber"/>. Only lines
    /// for which the model produced a usable, non-empty suggestion appear. Each value
    /// carries the same confidence/reason/provenance guarantees as the single-line
    /// method. Implementations MUST no-op (return an empty dictionary) when no AI
    /// provider/key is configured, when the line list is empty, or when the per-org
    /// monthly token cap is already reached.
    /// </returns>
    Task<IReadOnlyDictionary<int, AiMappingSuggestion>> SuggestSupplierItemCodesAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        IReadOnlyList<AiMappingLineContext> lines,
        IReadOnlyList<AiMappingCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>
    /// Refines source-column → canonical-PO-field mappings for the "magic mapping" UI.
    /// Given the available source columns and the canonical fields still lacking a
    /// confident deterministic match, returns AI-chosen (field, column) pairs.
    /// Implementations MUST no-op (return an empty list) when no AI key is configured,
    /// so callers can degrade gracefully to deterministic heuristics.
    /// </summary>
    Task<IReadOnlyList<AiFieldMappingSuggestion>> SuggestFieldMappingsAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> unresolvedCanonicalFields,
        CancellationToken ct = default);
}

/// <summary>
/// AI-chosen source-column → canonical-field pair returned by
/// <see cref="IAiMappingService.SuggestFieldMappingsAsync"/>.
/// </summary>
public sealed record AiFieldMappingSuggestion(
    string CanonicalField,
    string SuggestedColumn,
    float Confidence,
    string Reason);

public sealed record AiMappingLineContext(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    // The manufacturer part number + brand the source document states for this line, when it
    // states any. For a punchout order this is the ONLY identifier that means anything outside
    // the buying network, so withholding it from the model throws away the strongest signal on
    // the line. Appended last + defaulted so existing positional constructions are unaffected.
    string? ManufacturerPartNumber = null,
    string? ManufacturerName = null);

/// <summary>
/// A candidate supplier item code the model may suggest, plus the evidence behind it.
/// Two flavours share this record:
///   • a learned past resolution from <c>item_mappings</c> (<see cref="IsCatalogProduct"/> = false), and
///   • a real product from the supplier's <c>supplier_products</c> catalog
///     (<see cref="IsCatalogProduct"/> = true) — the authoritative "ground truth" set.
/// When ANY catalog candidate is present in a request, the implementation MUST constrain
/// the model to those real codes and REJECT any suggested code absent from the catalog set
/// (the allow-list guard). When no catalog candidate is present, behaviour is unchanged
/// (free suggestion grounded only by past mappings + the buyer line) — offer ⇔ works.
///
/// <see cref="ManufacturerPartNumber"/> makes a MANUFACTURER-code match count as a real match:
/// the model can recognise a catalog product by the manufacturer's number even when the
/// supplier's own code looks nothing like anything on the order line. The implementation MUST
/// treat a returned manufacturer part number as naming that candidate — resolving it back to the
/// candidate's <see cref="SupplierItemCode"/> — rather than rejecting it as a non-catalog code.
/// A manufacturer part number is NEVER itself a valid answer: the supplier's own code is.
/// </summary>
public sealed record AiMappingCandidate(
    string BuyerItemCode,
    string SupplierItemCode,
    string Provenance,
    bool IsCatalogProduct = false,
    string? Name = null,
    string? Unit = null,
    decimal? Price = null,
    string? Barcode = null,
    string? ManufacturerPartNumber = null,
    string? ManufacturerName = null);

public sealed record AiMappingSuggestion(
    string SupplierItemCode,
    float Confidence,
    string Reason,
    string Provenance);
