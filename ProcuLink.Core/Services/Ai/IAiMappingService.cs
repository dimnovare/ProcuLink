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

/// <summary>
/// A suggested supplier item code for an unresolved line, with the evidence behind it.
///
/// <para><b><see cref="Confidence"/> is null unless a model scored this suggestion.</b> It used to
/// be a non-nullable <c>float</c>, which left the two DETERMINISTIC producers in
/// <c>OrderIngestionService</c> no way to say "there is no score" — so both stamped a literal
/// <c>0.95f</c>: one for an exact supplier-catalog match on a manufacturer part number, one for a
/// plain echo of the part number the document itself states. No model ran on either. The number
/// then persisted to <c>PurchaseOrderLineEntity.AiSuggestionConfidence</c>, reached the order
/// passport, and rendered in the review UI as "AI confidence 95%" in the violet reserved for
/// AI-generated content. A deterministic lookup is a FACT about the supplier's catalog; scoring it
/// 95% understates it and misattributes it at the same time.</para>
///
/// <para><see cref="Basis"/> is how a caller says which kind of answer this is, so the UI can name
/// the evidence instead of scoring it. Same shape as <c>MappingSuggestionDto.Basis</c>, added by
/// the sibling fix for the saved-mapping path.</para>
/// </summary>
public sealed record AiMappingSuggestion(
    string SupplierItemCode,
    float? Confidence,
    string Reason,
    string Provenance,
    string Basis = AiMappingSuggestionBasis.Model);

/// <summary>Values for <see cref="AiMappingSuggestion.Basis"/>. Mirrored by the frontend's
/// <c>AiSuggestionBasis</c> union in src/types/procurement.ts.</summary>
public static class AiMappingSuggestionBasis
{
    /// <summary>An exact lookup in the supplier's own catalog. A fact, never a probability.</summary>
    public const string Catalog = "catalog";

    /// <summary>A code the source document itself states, echoed back. Also a fact, not a score.</summary>
    public const string SourceDocument = "source_document";

    /// <summary>Produced by a scorer; <see cref="AiMappingSuggestion.Confidence"/> carries its
    /// real number, and only this basis may carry one.</summary>
    public const string Model = "model";
}
