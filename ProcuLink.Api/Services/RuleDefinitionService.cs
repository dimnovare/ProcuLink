using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V4 read service for the unified validation model. Surfaces the org's reusable rule
/// definitions and a supplier's executable bindings. Pure reads — never evaluates and never mutates
/// (evaluation remains in <see cref="SupplierAcceptanceService"/>). All queries are org-scoped.
/// </summary>
public sealed class RuleDefinitionService : IRuleDefinitionService
{
    private readonly ProcuLinkDbContext _db;
    public RuleDefinitionService(ProcuLinkDbContext db) => _db = db;

    public async Task<IReadOnlyList<RuleDefinition>> ListDefinitionsAsync(Guid orgId, CancellationToken ct) =>
        await _db.RuleDefinitions
            .Where(d => d.OrgId == orgId)
            .OrderBy(d => d.Scope)
            .ThenBy(d => d.Code)
            .ToListAsync(ct);

    public async Task<RuleDefinition?> GetDefinitionAsync(Guid orgId, Guid definitionId, CancellationToken ct) =>
        await _db.RuleDefinitions
            .Where(d => d.OrgId == orgId && d.Id == definitionId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SupplierRuleBinding>> ListSupplierBindingsAsync(
        Guid orgId, Guid supplierId, CancellationToken ct)
    {
        // Active version if present, else latest non-archived (mirrors GetLatestAsync ordering).
        var profile = await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status != "archived")
            .OrderByDescending(p => p.Status == "active")
            .ThenByDescending(p => p.VersionNo)
            .FirstOrDefaultAsync(ct);

        if (profile is null || profile.Rules.Count == 0)
            return Array.Empty<SupplierRuleBinding>();

        // Resolve definitions referenced by these rules in one org-scoped query.
        var defIds = profile.Rules
            .Where(r => r.RuleDefinitionId is not null)
            .Select(r => r.RuleDefinitionId!.Value)
            .Distinct()
            .ToList();

        var defsById = defIds.Count == 0
            ? new Dictionary<Guid, RuleDefinition>()
            : (await _db.RuleDefinitions
                    .Where(d => d.OrgId == orgId && defIds.Contains(d.Id))
                    .ToListAsync(ct))
                .ToDictionary(d => d.Id);

        return profile.Rules
            .Select(r =>
            {
                RuleDefinition? def = null;
                if (r.RuleDefinitionId is not null) defsById.TryGetValue(r.RuleDefinitionId.Value, out def);
                return new SupplierRuleBinding(r, def);
            })
            .ToList();
    }
}
