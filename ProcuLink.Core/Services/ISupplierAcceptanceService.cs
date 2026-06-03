using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public record AcceptanceRuleInput(
    string Scope, string FieldPath, string Operator,
    string? ExpectedValue, string Severity, bool BlockOnFail);

public interface ISupplierAcceptanceService
{
    Task<SupplierAcceptanceProfile?> GetActiveAsync(Guid orgId, Guid supplierId, CancellationToken ct);
    Task<IReadOnlyList<SupplierAcceptanceProfile>> ListVersionsAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Creates a new draft version (next version number) with the given rules.</summary>
    Task<SupplierAcceptanceProfile> CreateVersionAsync(
        Guid orgId, Guid supplierId, string? protocol, string? outputFormat,
        IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct);

    /// <summary>Activates a version; archives the previously active one. Returns false if not found.</summary>
    Task<bool> ActivateVersionAsync(Guid orgId, Guid supplierId, int versionNo, CancellationToken ct);

    /// <summary>
    /// Evaluates the order against the supplier's active profile, persists + returns results.
    /// Returns null when the order does not exist for this org (caller should 404).
    /// An empty list means the order exists but has no active profile or no failing rules.
    /// </summary>
    Task<IReadOnlyList<OrderValidationResult>?> ValidateOrderAsync(Guid orgId, Guid orderId, CancellationToken ct);
}
