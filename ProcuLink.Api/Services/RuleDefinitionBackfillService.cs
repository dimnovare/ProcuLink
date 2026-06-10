using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V4 backfill (zero behaviour change). Seeds the well-known global rule catalog as
/// org-scoped <see cref="RuleDefinition"/>s and links existing free-floating
/// <see cref="SupplierAcceptanceRule"/> rows to a matching definition. The acceptance EXECUTOR
/// (<see cref="SupplierAcceptanceService"/>) keeps reading the rule rows' own scalar columns, which
/// this service never mutates — so evaluation is byte-identical before and after.
///
/// <para>EF-only (no raw SQL). Idempotent on two axes: the UNIQUE(org_id, code) definition guard
/// makes re-seeding a no-op, and the link step only touches rules whose <c>RuleDefinitionId</c> is
/// still null. Safe to call on every boot / under deploy + Hangfire retries.</para>
///
/// <para>No circular FK: a <see cref="RuleDefinition"/> references only the organisation, and a
/// rule references a definition by a nullable FK — definitions are inserted (and saved) before any
/// rule is linked, so insert order is never ambiguous.</para>
/// </summary>
public sealed class RuleDefinitionBackfillService : IRuleDefinitionBackfillService
{
    private readonly ProcuLinkDbContext _db;
    public RuleDefinitionBackfillService(ProcuLinkDbContext db) => _db = db;

    public async Task<(int definitionsCreated, int rulesLinked)> BackfillAllAsync(CancellationToken ct)
    {
        var definitionsCreated = 0;
        var rulesLinked = 0;

        // 1) Seed the catalog for every org that has any data we'd want a catalog for.
        //    Union the orgs we know about (suppliers OR acceptance profiles) so a brand-new org
        //    with no config yet isn't seeded prematurely (it'll be seeded at first profile create).
        var orgIds = await _db.SupplierAcceptanceProfiles.Select(p => p.OrgId)
            .Concat(_db.Suppliers.Select(s => s.OrgId))
            .Distinct()
            .ToListAsync(ct);

        foreach (var orgId in orgIds)
        {
            try
            {
                definitionsCreated += await SeedOrgCatalogAsync(orgId, ct);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                Console.Error.WriteLine($"[RuleDefinitionBackfill] seed org {orgId} failed: {ex.Message}");
            }
        }

        // 2) Link every still-unlinked acceptance rule to a definition (creating a derived
        //    org-scoped definition when no catalog entry matches the rule's field/operator).
        //    Process per (org, profile) so a single bad rule can't abort the whole batch.
        var unlinked = await (
            from r in _db.SupplierAcceptanceRules
            join p in _db.SupplierAcceptanceProfiles on r.ProfileId equals p.Id
            where r.RuleDefinitionId == null
            select new { Rule = r, p.OrgId }).ToListAsync(ct);

        // Group by org to resolve/create definitions once per org.
        foreach (var orgGroup in unlinked.GroupBy(x => x.OrgId))
        {
            try
            {
                rulesLinked += await LinkOrgRulesAsync(orgGroup.Key, orgGroup.Select(x => x.Rule).ToList(), ct);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                Console.Error.WriteLine($"[RuleDefinitionBackfill] link org {orgGroup.Key} failed: {ex.Message}");
            }
        }

        return (definitionsCreated, rulesLinked);
    }

    public async Task<int> SeedOrgCatalogAsync(Guid orgId, CancellationToken ct)
    {
        var existingCodes = await _db.RuleDefinitions
            .Where(d => d.OrgId == orgId)
            .Select(d => d.Code)
            .ToListAsync(ct);
        var existing = existingCodes.ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var created = 0;
        foreach (var entry in RuleCatalog.Entries)
        {
            if (existing.Contains(entry.Code)) continue;
            _db.RuleDefinitions.Add(new RuleDefinition
            {
                Id                   = Guid.NewGuid(),
                OrgId                = orgId,
                Code                 = entry.Code,
                Title                = entry.Title,
                Description          = entry.Description,
                Scope                = entry.Scope,
                FieldPath            = entry.FieldPath,
                Operator             = entry.Operator,
                DefaultSeverity      = entry.DefaultSeverity,
                DefaultExpectedValue = entry.DefaultExpectedValue,
                ParamHint            = entry.ParamHint,
                UblRef               = entry.UblRef,
                EdifactRef           = entry.EdifactRef,
                X12Ref               = entry.X12Ref,
                CxmlRef              = entry.CxmlRef,
                IsSystem             = true,
                CreatedBy            = "system:seed",
                CreatedAt            = now,
            });
            existing.Add(entry.Code); // guard against duplicate entries in the catalog list
            created++;
        }
        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// Links the given rules for one org to definitions, creating any missing org-scoped definitions
    /// FIRST (and saving them) before assigning rule FKs — so definitions always exist when referenced.
    /// </summary>
    private async Task<int> LinkOrgRulesAsync(Guid orgId, IReadOnlyList<SupplierAcceptanceRule> rules, CancellationToken ct)
    {
        // Load the org's existing definitions keyed by code.
        var defsByCode = (await _db.RuleDefinitions
                .Where(d => d.OrgId == orgId)
                .ToListAsync(ct))
            .ToDictionary(d => d.Code, StringComparer.Ordinal);

        // First pass: ensure a definition exists for every distinct (fieldPath, operator) the rules use.
        var now = DateTime.UtcNow;
        var newDefs = new List<RuleDefinition>();
        foreach (var rule in rules)
        {
            var code = RuleCatalog.CodeFor(rule.FieldPath, rule.Operator);
            if (defsByCode.ContainsKey(code)) continue;
            if (newDefs.Any(d => d.Code == code)) continue;

            // No catalog/seed match — derive a definition from the existing rule (custom org rule).
            newDefs.Add(new RuleDefinition
            {
                Id                   = Guid.NewGuid(),
                OrgId                = orgId,
                Code                 = code,
                Title                = $"{rule.FieldPath} {rule.Operator}",
                Description          = "Imported from an existing supplier acceptance rule (Group V4 backfill).",
                Scope                = rule.Scope,
                FieldPath            = rule.FieldPath,
                Operator             = rule.Operator,
                DefaultSeverity      = rule.Severity,
                DefaultExpectedValue = rule.ExpectedValue,
                ParamHint            = null,
                IsSystem             = false,
                CreatedBy            = "system:backfill",
                CreatedAt            = now,
            });
        }
        if (newDefs.Count > 0)
        {
            _db.RuleDefinitions.AddRange(newDefs);
            await _db.SaveChangesAsync(ct); // definitions exist before any rule references them
            foreach (var d in newDefs) defsByCode[d.Code] = d;
        }

        // Second pass: link each rule to its definition (NEVER touching the rule's scalar columns).
        var linked = 0;
        foreach (var rule in rules)
        {
            var code = RuleCatalog.CodeFor(rule.FieldPath, rule.Operator);
            if (!defsByCode.TryGetValue(code, out var def)) continue; // should not happen
            rule.RuleDefinitionId = def.Id;
            rule.RuleCode         = def.Code;
            linked++;
        }
        if (linked > 0) await _db.SaveChangesAsync(ct);
        return linked;
    }
}
