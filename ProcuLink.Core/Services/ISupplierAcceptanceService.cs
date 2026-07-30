using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public record AcceptanceRuleInput(
    string Scope, string FieldPath, string Operator,
    string? ExpectedValue, string Severity, bool BlockOnFail);

public interface ISupplierAcceptanceService
{
    Task<SupplierAcceptanceProfile?> GetActiveAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Returns the active version if one exists; otherwise the latest non-archived draft.</summary>
    Task<SupplierAcceptanceProfile?> GetLatestAsync(Guid orgId, Guid supplierId, CancellationToken ct);
    Task<IReadOnlyList<SupplierAcceptanceProfile>> ListVersionsAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>Creates a new draft version (next version number) with the given rules.</summary>
    Task<SupplierAcceptanceProfile> CreateVersionAsync(
        Guid orgId, Guid supplierId, string? protocol, string? outputFormat,
        IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct);

    /// <summary>Activates a version; archives the previously active one. Returns false if not found.</summary>
    Task<bool> ActivateVersionAsync(Guid orgId, Guid supplierId, int versionNo, CancellationToken ct);

    /// <summary>
    /// Evaluates the order against the supplier's active profile (invariants + acceptance rules +
    /// output-render checks), persists + returns results. Returns null when the order does not exist
    /// for this org (caller should 404). An empty list means the order exists but has no active
    /// profile or failing rules. <paramref name="outputFormat"/> (optional) lets the output checks
    /// catch the format-mandatory buyer item code; when omitted, the price check still surfaces.
    /// </summary>
    Task<IReadOnlyList<OrderValidationResult>?> ValidateOrderAsync(
        Guid orgId, Guid orderId, CancellationToken ct, OutputFormat? outputFormat = null);

    /// <summary>
    /// WP-17 — the BLOCKING subset of <see cref="ValidateOrderAsync"/>, evaluated against the SAME
    /// effective (pinned-revision-aware) acceptance profile, but PURE: it persists nothing, so the
    /// transform path can consult it without rewriting the order's stored validation state.
    ///
    /// <para>A failure blocks when its rule's severity is <c>error</c> OR the rule sets
    /// <c>BlockOnFail</c> — the exact promise the supplier-profile UI makes. Invariant
    /// (<c>invariant.*</c>) and output-render (<c>output.*</c>) rows are deliberately EXCLUDED: they
    /// carry no rule and are advisory, and enforcing them would start refusing orders that deliver
    /// fine today.</para>
    ///
    /// <para>Returns null when the order does not exist for this organisation; an EMPTY list means
    /// the order exists and nothing refuses it.</para>
    /// </summary>
    Task<IReadOnlyList<AcceptanceBlocker>?> GetBlockingFailuresAsync(
        Guid orgId, Guid orderId, CancellationToken ct);
}
