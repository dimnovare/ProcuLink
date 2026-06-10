using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

public sealed class SupplierAcceptanceService : ISupplierAcceptanceService
{
    private readonly ProcuLinkDbContext _db;
    public SupplierAcceptanceService(ProcuLinkDbContext db) => _db = db;

    public async Task<SupplierAcceptanceProfile?> GetActiveAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status == "active")
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierAcceptanceProfile?> GetLatestAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status != "archived")
            .OrderByDescending(p => p.Status == "active")
            .ThenByDescending(p => p.VersionNo)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SupplierAcceptanceProfile>> ListVersionsAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .OrderByDescending(p => p.VersionNo)
            .ToListAsync(ct);

    public async Task<SupplierAcceptanceProfile> CreateVersionAsync(
        Guid orgId, Guid supplierId, string? protocol, string? outputFormat,
        IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct)
    {
        var maxVersion = await _db.SupplierAcceptanceProfiles
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .Select(p => (int?)p.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        // Group V4: bind new rules to reusable RuleDefinitions so they are no longer free-floating.
        // Definitions referenced by the rules must exist FIRST (a rule's RuleDefinitionId is a
        // nullable FK to a definition) — resolve/create + save them before the profile that points
        // at them. This NEVER affects evaluation: the executor reads the rule scalar columns below,
        // which come verbatim from the input. RuleDefinitionId/RuleCode are pure provenance metadata.
        var definitionIdByCode = await ResolveDefinitionsAsync(orgId, rules, createdBy, ct);

        var profile = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = nextVersion, Status = "draft",
            Protocol = protocol, OutputFormat = outputFormat,
            CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
            Rules = rules.Select(r =>
            {
                var code = RuleCatalog.CodeFor(r.FieldPath, r.Operator);
                return new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = r.Scope, FieldPath = r.FieldPath,
                    Operator = r.Operator, ExpectedValue = r.ExpectedValue,
                    Severity = r.Severity, BlockOnFail = r.BlockOnFail,
                    RuleDefinitionId = definitionIdByCode.TryGetValue(code, out var did) ? did : (Guid?)null,
                    RuleCode = definitionIdByCode.ContainsKey(code) ? code : null,
                };
            }).ToList(),
        };
        _db.SupplierAcceptanceProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    /// <summary>
    /// Group V4: ensures an org-scoped <see cref="RuleDefinition"/> exists for every distinct
    /// (fieldPath, operator) the given rules use, creating + saving any missing ones (derived from a
    /// seeded catalog template when one matches, else from the rule's own shape). Returns a
    /// code → definitionId map so callers can bind each rule. Definitions are saved here, BEFORE the
    /// rules that reference them, so there is never an ambiguous circular insert.
    /// </summary>
    private async Task<Dictionary<string, Guid>> ResolveDefinitionsAsync(
        Guid orgId, IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct)
    {
        var wantedCodes = rules
            .Select(r => RuleCatalog.CodeFor(r.FieldPath, r.Operator))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existing = await _db.RuleDefinitions
            .Where(d => d.OrgId == orgId && wantedCodes.Contains(d.Code))
            .ToListAsync(ct);
        var idByCode = existing.ToDictionary(d => d.Code, d => d.Id, StringComparer.Ordinal);

        var seedByCode = RuleCatalog.Entries.ToDictionary(e => e.Code, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var toAdd = new List<RuleDefinition>();
        foreach (var r in rules)
        {
            var code = RuleCatalog.CodeFor(r.FieldPath, r.Operator);
            if (idByCode.ContainsKey(code) || toAdd.Any(d => d.Code == code)) continue;

            RuleDefinition def;
            if (seedByCode.TryGetValue(code, out var seed))
            {
                def = new RuleDefinition
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Code = seed.Code, Title = seed.Title,
                    Description = seed.Description, Scope = seed.Scope, FieldPath = seed.FieldPath,
                    Operator = seed.Operator, DefaultSeverity = seed.DefaultSeverity,
                    DefaultExpectedValue = seed.DefaultExpectedValue, ParamHint = seed.ParamHint,
                    UblRef = seed.UblRef, EdifactRef = seed.EdifactRef, X12Ref = seed.X12Ref,
                    CxmlRef = seed.CxmlRef, IsSystem = true, CreatedBy = "system:seed", CreatedAt = now,
                };
            }
            else
            {
                def = new RuleDefinition
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Code = code,
                    Title = $"{r.FieldPath} {r.Operator}",
                    Description = "Created from a supplier acceptance rule (Group V4).",
                    Scope = r.Scope, FieldPath = r.FieldPath, Operator = r.Operator,
                    DefaultSeverity = r.Severity, DefaultExpectedValue = r.ExpectedValue,
                    IsSystem = false, CreatedBy = createdBy ?? "system", CreatedAt = now,
                };
            }
            toAdd.Add(def);
        }

        if (toAdd.Count > 0)
        {
            _db.RuleDefinitions.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
            foreach (var d in toAdd) idByCode[d.Code] = d.Id;
        }
        return idByCode;
    }

    public async Task<bool> ActivateVersionAsync(Guid orgId, Guid supplierId, int versionNo, CancellationToken ct)
    {
        var versions = await _db.SupplierAcceptanceProfiles
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .ToListAsync(ct);
        var target = versions.FirstOrDefault(p => p.VersionNo == versionNo);
        if (target is null) return false;

        var now = DateTime.UtcNow;
        foreach (var v in versions)
        {
            if (v.Status == "active" && v.Id != target.Id)
            {
                v.Status = "archived";
                v.EffectiveTo = now;
            }
        }
        target.Status = "active";
        target.EffectiveFrom = now;
        target.EffectiveTo = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<OrderValidationResult>?> ValidateOrderAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Include(o => o.Lines)
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (order is null) return null;

        var profile = await GetActiveAsync(orgId, order.SupplierId, ct);
        var now = DateTime.UtcNow;

        // Re-validation overwrites prior results for this order.
        var prior = _db.OrderValidationResults.Where(r => r.OrgId == orgId && r.OrderId == orderId);
        _db.OrderValidationResults.RemoveRange(prior);

        var results = EvaluateProfile(orgId, orderId, profile, order, now);

        _db.OrderValidationResults.AddRange(results);
        await _db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>
    /// Pure, NON-MUTATING evaluation of an acceptance <paramref name="profile"/> against a loaded
    /// <paramref name="order"/>. Produces the same <see cref="OrderValidationResult"/> rows
    /// <see cref="ValidateOrderAsync"/> persists, but writes nothing to the database. Reused by the
    /// V2 replay path so a DRAFT connection revision's bound validation can be evaluated against a
    /// historical order WITHOUT touching its stored validation state. A null profile yields an empty
    /// list (no active validation). The returned rows are detached (not added to any DbSet).
    /// </summary>
    public static IReadOnlyList<OrderValidationResult> EvaluateProfile(
        Guid orgId, Guid orderId, SupplierAcceptanceProfile? profile, PurchaseOrderEntity order, DateTime now)
    {
        var results = new List<OrderValidationResult>();
        if (profile is null) return results;

        foreach (var rule in profile.Rules)
        {
            if (rule.Scope == "order")
            {
                var (pass, val) = EvaluateOrderField(order, rule);
                results.Add(MakeResult(orgId, orderId, profile.Id, rule, null, pass, val, now));
            }
            else
            {
                foreach (var line in order.Lines)
                {
                    var (pass, val) = EvaluateLineField(line, rule);
                    results.Add(MakeResult(orgId, orderId, profile.Id, rule, line.LineNumber, pass, val, now));
                }
            }
        }
        return results;
    }

    private static OrderValidationResult MakeResult(
        Guid orgId, Guid orderId, Guid profileId, SupplierAcceptanceRule rule,
        int? lineNumber, bool pass, string? actualValue, DateTime now) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
        ProfileId = profileId, RuleId = rule.Id, LineNumber = lineNumber,
        Severity = rule.Severity, Status = pass ? "pass" : "fail",
        Code = $"{rule.FieldPath}.{rule.Operator}",
        Message = pass
            ? $"{rule.FieldPath} satisfies {rule.Operator}"
            : $"{rule.FieldPath} ('{actualValue}') failed rule {rule.Operator} {rule.ExpectedValue}",
        DetectedAt = now,
    };

    private static (bool pass, string? value) EvaluateOrderField(PurchaseOrderEntity o, SupplierAcceptanceRule rule)
    {
        string? v = rule.FieldPath switch
        {
            "currency"  => o.Currency,
            "buyerName" => o.BuyerName,
            _           => null,
        };
        return (Evaluate(rule, v), v);
    }

    private static (bool pass, string? value) EvaluateLineField(PurchaseOrderLineEntity l, SupplierAcceptanceRule rule)
    {
        string? v = rule.FieldPath switch
        {
            "supplierItemCode" => l.SupplierItemCode,
            "buyerItemCode"    => l.BuyerItemCode,
            "description"      => l.Description,
            "quantity"         => l.Quantity.ToString(CultureInfo.InvariantCulture),
            "unitPrice"        => l.UnitPrice.ToString(CultureInfo.InvariantCulture),
            _                  => null,
        };
        return (Evaluate(rule, v), v);
    }

    private static bool Evaluate(SupplierAcceptanceRule rule, string? actual)
    {
        switch (rule.Operator)
        {
            case "required":
                return !string.IsNullOrWhiteSpace(actual);
            case "equals":
                return string.Equals(actual, rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
            case "in":
                var allowed = (rule.ExpectedValue ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return actual is not null && allowed.Contains(actual, StringComparer.OrdinalIgnoreCase);
            case "min":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a1)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m1)
                    && a1 >= m1;
            case "max":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a2)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m2)
                    && a2 <= m2;
            case "not_equals":
                return !string.Equals(actual, rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
            case "contains":
                return actual is not null
                    && actual.Contains(rule.ExpectedValue ?? "", StringComparison.OrdinalIgnoreCase);
            case "greater_than":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a3)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m3)
                    && a3 > m3;
            case "less_than":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a4)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m4)
                    && a4 < m4;
            case "max_length":
                return actual is not null
                    && int.TryParse(rule.ExpectedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var maxLen)
                    && actual.Length <= maxLen;
            default:
                return true; // unknown operator → non-blocking pass
        }
    }
}
