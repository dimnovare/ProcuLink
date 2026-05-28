using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class ValidationRuleService : IValidationRuleService
{
    private readonly ProcuLinkDbContext _db;

    private static readonly CreateRuleRequest[] DefaultRules =
    [
        new("Missing unit price",    "Line item has no unit price set. Order cannot be delivered without valid pricing.",   "error",   "Line item", Enabled: true,  AutoBlock: true),
        new("Unknown buyer code",    "Buyer product code not found in mapping table. Manual mapping required.",             "error",   "Line item", Enabled: true,  AutoBlock: true),
        new("Order total mismatch",  "Sum of line amounts does not match the header total. Tolerance: ±€0.01.",            "error",   "Amount",    Enabled: true,  AutoBlock: true),
        new("Missing PO number",     "Purchase order number is absent. Required for supplier processing.",                  "error",   "Header",    Enabled: true,  AutoBlock: false),
        new("Delivery date in past", "Requested delivery date is in the past. Supplier may reject the order.",             "warning", "Header",    Enabled: true,  AutoBlock: false),
        new("Duplicate PO number",   "A PO with this number was already processed in the last 30 days.",                   "warning", "Header",    Enabled: true,  AutoBlock: false),
    ];

    public ValidationRuleService(ProcuLinkDbContext db) { _db = db; }

    public async Task<IReadOnlyList<ValidationRule>> ListAsync(Guid orgId, CancellationToken ct)
    {
        var count = await _db.ValidationRules
            .CountAsync(r => r.OrgId == orgId, ct);

        if (count == 0)
        {
            foreach (var req in DefaultRules)
                await CreateAsync(orgId, req, ct);
        }

        return await _db.ValidationRules
            .AsNoTracking()
            .Where(r => r.OrgId == orgId)
            .OrderBy(r => r.Severity == "error"   ? 0 : r.Severity == "warning" ? 1 : 2)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<ValidationRule> CreateAsync(Guid orgId, CreateRuleRequest req, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rule = new ValidationRule
        {
            Id          = Guid.NewGuid(),
            OrgId       = orgId,
            Name        = req.Name,
            Description = req.Description,
            Severity    = req.Severity,
            Entity      = req.Entity,
            Enabled     = req.Enabled,
            AutoBlock   = req.AutoBlock,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _db.ValidationRules.Add(rule);
        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<ValidationRule?> UpdateAsync(Guid orgId, Guid id, CreateRuleRequest req, CancellationToken ct)
    {
        var rule = await _db.ValidationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);

        if (rule is null) return null;

        rule.Name        = req.Name;
        rule.Description = req.Description;
        rule.Severity    = req.Severity;
        rule.Entity      = req.Entity;
        rule.Enabled     = req.Enabled;
        rule.AutoBlock   = req.AutoBlock;
        rule.UpdatedAt   = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<ValidationRule?> ToggleAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var rule = await _db.ValidationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);

        if (rule is null) return null;

        rule.Enabled   = !rule.Enabled;
        rule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var rule = await _db.ValidationRules
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);

        if (rule is null) return false;

        _db.ValidationRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
