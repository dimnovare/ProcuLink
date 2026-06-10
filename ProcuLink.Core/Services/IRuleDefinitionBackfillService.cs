namespace ProcuLink.Core.Services;

/// <summary>
/// Group V4 — unifies the descriptive global rule catalog with the executable acceptance rules,
/// with ZERO behaviour change to evaluation. Idempotent; runs on boot like the V1 connection
/// backfill (Hangfire/restart culture — safe to re-run).
///
/// <para>Two responsibilities, both idempotent:</para>
/// <list type="number">
///   <item><b>Seed</b> the well-known global-catalog rules (from
///   <see cref="ProcuLink.Core.Entities.RuleCatalog"/>) as <see cref="ProcuLink.Core.Entities.RuleDefinition"/>
///   rows for every org that doesn't already have them (UNIQUE(org_id, code) guards re-runs).</item>
///   <item><b>Link</b> each existing free-floating <see cref="ProcuLink.Core.Entities.SupplierAcceptanceRule"/>
///   to a matching definition (creating an org-scoped definition derived from the rule if no
///   catalog entry matches), setting the rule's <c>RuleDefinitionId</c> + <c>RuleCode</c>. The rule's
///   own scalar columns are NEVER changed, so the executor produces identical results.</item>
/// </list>
/// </summary>
public interface IRuleDefinitionBackfillService
{
    /// <summary>
    /// Seed catalog definitions for every org, then link every still-unlinked acceptance rule to a
    /// definition. Returns (definitionsCreated, rulesLinked) — both 0 on a fully idempotent re-run.
    /// </summary>
    Task<(int definitionsCreated, int rulesLinked)> BackfillAllAsync(CancellationToken ct);

    /// <summary>
    /// Ensure the well-known catalog definitions exist for one org (idempotent). Returns the number
    /// of new definition rows created. Useful at org-create time so a fresh org has a usable catalog.
    /// </summary>
    Task<int> SeedOrgCatalogAsync(Guid orgId, CancellationToken ct);
}
