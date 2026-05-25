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
}

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
