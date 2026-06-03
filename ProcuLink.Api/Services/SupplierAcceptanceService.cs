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

        var profile = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = nextVersion, Status = "draft",
            Protocol = protocol, OutputFormat = outputFormat,
            CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
            Rules = rules.Select(r => new SupplierAcceptanceRule
            {
                Id = Guid.NewGuid(), Scope = r.Scope, FieldPath = r.FieldPath,
                Operator = r.Operator, ExpectedValue = r.ExpectedValue,
                Severity = r.Severity, BlockOnFail = r.BlockOnFail,
            }).ToList(),
        };
        _db.SupplierAcceptanceProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile;
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

        var results = new List<OrderValidationResult>();

        if (profile is not null)
        {
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
        }

        _db.OrderValidationResults.AddRange(results);
        await _db.SaveChangesAsync(ct);
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
            default:
                return true; // unknown operator → non-blocking pass
        }
    }
}
