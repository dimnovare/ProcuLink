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
    string? Unit);

public sealed record AiMappingCandidate(
    string BuyerItemCode,
    string SupplierItemCode,
    string Provenance);

public sealed record AiMappingSuggestion(
    string SupplierItemCode,
    float Confidence,
    string Reason,
    string Provenance);
