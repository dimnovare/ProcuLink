using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Api.Services.Documents;

public sealed record LookupUse(Guid FlowId, string FlowName);

public static class DocumentLookups
{
    /// <summary>Every Lookup step in a template, whatever its params.</summary>
    public static IEnumerable<ManipulatorEntry> Steps(OutputNodeTemplate template)
    {
        IEnumerable<ManipulatorEntry> Walk(OutputNode? n) =>
            n is null ? [] : (n.Rule?.FieldManipulators ?? []).Where(m => m?.Type == "Lookup").Concat((n.Children ?? []).SelectMany(Walk));
        return template.Root is null ? [] : Walk(template.Root);
    }

    /// <summary>Ids of the lookups a template's Lookup steps point at; unparsable ids are skipped.</summary>
    public static IReadOnlySet<Guid> UsedBy(OutputNodeTemplate template) =>
        Steps(template).Select(m => Guid.TryParse((m.Params ?? []).ElementAtOrDefault(0), out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet();

    /// <summary>For each of this organisation's lookups that something uses: the flows whose editable or live outputs have a
    /// Lookup step naming it, or whose editable or live fields have a check on it. Each flow once, ordered by flow name (ordinal), then id.</summary>
    public static async Task<ILookup<Guid, LookupUse>> UsesAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct)
    {
        var editable = await db.Set<FlowOutput>().AsNoTracking().Where(o => o.OrgId == orgId)
            .Select(o => new { o.FlowId, o.TemplateJson }).ToListAsync(ct);
        var snapshots = await FlowVersions.LiveSnapshotsAsync(db, orgId, ct);
        var live = snapshots.SelectMany(s => (s.Snapshot.Outputs ?? []).Select(o => new { s.FlowId, o.TemplateJson }));
        var fields = (await db.Flows.PersonFlows(orgId).AsNoTracking().Select(f => new { FlowId = f.Id, f.FieldsJson }).ToListAsync(ct))
            .Select(f => (f.FlowId, Fields: DocumentJson.Deserialize<FlowFieldList>(f.FieldsJson)))
            .Concat(snapshots.Select(s => (s.FlowId, s.Snapshot.Fields)));
        var uses = editable.Concat(live)
            .SelectMany(o => UsedBy(DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson)).Select(lookup => (Lookup: lookup, o.FlowId)))
            .Concat(fields.SelectMany(f => FieldChecks.LookupIds(f.Fields).Select(lookup => (Lookup: lookup, f.FlowId))))
            .Distinct().ToList();
        var flowIds = uses.Select(u => u.FlowId).Distinct().ToList();
        var names = flowIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Flows.PersonFlows(orgId).AsNoTracking().Where(f => flowIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name }).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        // A use whose flow is not one of this organisation's is left out.
        return uses.Where(u => names.ContainsKey(u.FlowId))
            .OrderBy(u => names[u.FlowId], StringComparer.Ordinal).ThenBy(u => u.FlowId)
            .ToLookup(u => u.Lookup, u => new LookupUse(u.FlowId, names[u.FlowId]));
    }

    /// <summary>The org's lookups the flow's checks point at; no query when no check uses one.</summary>
    public static async Task<IReadOnlyDictionary<Guid, CheckLookup>> ForChecksAsync(ProcuLinkDbContext db, Guid orgId, FlowFieldList fields, CancellationToken ct)
    {
        var ids = FieldChecks.LookupIds(fields).ToList();
        if (ids.Count == 0) return new Dictionary<Guid, CheckLookup>();
        var rows = await db.Set<Lookup>().AsNoTracking().Where(l => l.OrgId == orgId && ids.Contains(l.Id)).Select(l => new { l.Id, l.Name, l.EntriesJson }).ToListAsync(ct);
        return rows.ToDictionary(l => l.Id, l => new CheckLookup(l.Name,
            DocumentJson.Deserialize<List<LookupEntry>>(l.EntriesJson).Select(e => e.From.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>The org's lookups the templates point at, loaded once for a render.</summary>
    public static async Task<LookupTables> LoadAsync(ProcuLinkDbContext db, Guid orgId, IEnumerable<OutputNodeTemplate> templates, CancellationToken ct)
    {
        var ids = templates.SelectMany(UsedBy).Distinct().ToList();
        if (ids.Count == 0) return LookupTables.Empty;
        var rows = await db.Set<Lookup>().AsNoTracking().Where(l => l.OrgId == orgId && ids.Contains(l.Id)).ToListAsync(ct);
        return new(rows.ToDictionary(l => l.Id, l => (IReadOnlyDictionary<string, string>)
            DocumentJson.Deserialize<List<LookupEntry>>(l.EntriesJson).ToDictionary(e => e.From, e => e.To)));
    }
}
