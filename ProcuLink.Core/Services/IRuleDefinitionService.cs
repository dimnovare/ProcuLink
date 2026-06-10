using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// A supplier's executable acceptance rule together with the reusable definition it binds to
/// (Group V4). The rule scalar fields are the per-binding values the executor reads; the definition
/// supplies the catalog metadata (title, description, defaults, standards refs). <see cref="Definition"/>
/// is null for a legacy rule that the backfill could not link (should be rare after backfill runs).
/// </summary>
public sealed record SupplierRuleBinding(
    SupplierAcceptanceRule Rule, RuleDefinition? Definition);

/// <summary>
/// Group V4 — read access to the unified validation model. Lists the org's reusable rule
/// definitions (the catalog) and a supplier's executable bindings (its active acceptance rules
/// joined to their definitions). This does NOT evaluate anything — evaluation stays in
/// <see cref="ISupplierAcceptanceService"/>. All methods are org-scoped.
/// </summary>
public interface IRuleDefinitionService
{
    /// <summary>All rule definitions for the org, ordered by scope then code.</summary>
    Task<IReadOnlyList<RuleDefinition>> ListDefinitionsAsync(Guid orgId, CancellationToken ct);

    /// <summary>A single definition by id, or null if not in this org.</summary>
    Task<RuleDefinition?> GetDefinitionAsync(Guid orgId, Guid definitionId, CancellationToken ct);

    /// <summary>
    /// The supplier's active acceptance rules as bindings (each joined to its definition). Empty when
    /// the supplier has no active profile. Uses the active profile if present, else the latest
    /// non-archived version (mirrors <see cref="ISupplierAcceptanceService.GetLatestAsync"/>).
    /// </summary>
    Task<IReadOnlyList<SupplierRuleBinding>> ListSupplierBindingsAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
