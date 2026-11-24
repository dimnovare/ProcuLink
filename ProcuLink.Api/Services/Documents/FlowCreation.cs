using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>What creating a flow needs: a valid name, valid fields, room under the plan's flow cap, and the new row.</summary>
public static class FlowCreation
{
    public static bool TryName(string? raw, out string name)
    {
        name = raw?.Trim() ?? "";
        return name.Length is >= 1 and <= 120;
    }

    /// <summary>The first problem found in a field list, or null when it is valid.</summary>
    public static string? InvalidFields(FlowFieldList list)
    {
        if (list.Fields.Any(f => f is null) || list.Tables.Any(t => t?.Columns is null || t.Columns.Any(c => c is null)))
            return "Fields, tables and columns must not be null.";
        foreach (var t in list.Tables)
            if (t.Key is null || !FieldKey.Pattern.IsMatch(t.Key)) return $"Table key '{t.Key}' must start with a lowercase letter and use only a-z, 0-9 and _ (max 64 characters).";
        foreach (var f in list.Fields.Concat(list.Tables.SelectMany(t => t.Columns)))
        {
            if (f.Key is null || !FieldKey.Pattern.IsMatch(f.Key))
                return $"Key '{f.Key}' must start with a lowercase letter and use only a-z, 0-9 and _ (max 64 characters).";
            if (f.Type is null || !FieldTypes.All.Contains(f.Type)) return $"Type '{f.Type}' is not supported.";
            if (f.Meaning is not null && !FieldMeanings.All.Contains(f.Meaning)) return $"Meaning '{f.Meaning}' is not supported.";
        }
        if (list.Fields.Select(f => FieldChecks.Problem(f, isColumn: false))
                .Concat(list.Tables.SelectMany(t => t.Columns).Select(c => FieldChecks.Problem(c, isColumn: true)))
                .FirstOrDefault(p => p is not null) is { } check) return check;
        var dup = list.Fields.Select(f => f.Key).GroupBy(k => k).FirstOrDefault(g => g.Count() > 1)?.Key
            ?? list.Tables.Select(t => t.Key).GroupBy(k => k).FirstOrDefault(g => g.Count() > 1)?.Key;
        dup ??= list.Tables.SelectMany(t => t.Columns.GroupBy(c => c.Key).Where(g => g.Count() > 1).Select(g => g.Key)).FirstOrDefault();
        return dup is null ? null : $"Key '{dup}' is used more than once.";
    }

    /// <summary>A field list as it is saved: checks tidied, then its first problem, a lookup that is not this organisation's included.</summary>
    public static async Task<(FlowFieldList Fields, string? Problem)> CheckFieldsAsync(ProcuLinkDbContext db, Guid orgId, FlowFieldList fields, CancellationToken ct)
    {
        fields = FieldChecks.Normalise(fields);
        if (InvalidFields(fields) is { } detail) return (fields, detail);
        var lookupIds = FieldChecks.LookupIds(fields).ToList();
        return lookupIds.Count > 0 && await db.Set<Lookup>().CountAsync(l => l.OrgId == orgId && lookupIds.Contains(l.Id), ct) != lookupIds.Count
            ? (fields, "That lookup was not found. Choose another one.")
            : (fields, null);
    }

    /// <summary>The plan and its cap when this organisation's person flows already fill it; null when another flow fits.</summary>
    public static async Task<(string Plan, int Cap)?> LimitReachedAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct)
    {
        var plan = await db.Organisations.Where(o => o.Id == orgId).Select(o => o.Plan).FirstOrDefaultAsync(ct) ?? PlanConstants.Pilot;
        return PlanConstants.GetFlowLimit(plan) is int cap && await db.Flows.PersonFlows(orgId).CountAsync(ct) >= cap
            ? (plan, cap)
            : null;
    }

    /// <summary>A new flow. <paramref name="automation"/> has no default: a mode that sends by itself is an admin's choice, so a caller passes
    /// <see cref="FlowAutomation.Marked"/> only behind <c>RequireOrgAdminAttribute.IsAdmin</c>, and <see cref="FlowAutomation.Review"/> otherwise.</summary>
    public static Flow New(Guid orgId, string name, FlowFieldList fields, bool confirmed, string automation)
    {
        var now = DateTime.UtcNow;
        return new Flow
        {
            Id = Guid.NewGuid(), OrgId = orgId, Name = name, FieldsJson = DocumentJson.Serialize(fields),
            FieldsConfirmed = confirmed, Automation = automation, CreatedAt = now, UpdatedAt = now,
        };
    }
}
