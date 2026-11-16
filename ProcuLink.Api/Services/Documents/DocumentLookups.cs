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

    /// <summary>For each of this organisation's lookups that something uses: the flows whose editable outputs or live
    /// version's outputs have a Lookup step naming it. Each flow once, ordered by flow name (ordinal), then id.</summary>
    public static async Task<ILookup<Guid, LookupUse>> UsesAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct)
    {
        var editable = await db.Set<FlowOutput>().AsNoTracking().Where(o => o.OrgId == orgId)
            .Select(o => new { o.FlowId, o.TemplateJson }).ToListAsync(ct);
        var live = (await FlowVersions.LiveVersionOutputsByFlowAsync(db, orgId, ct)).Select(x => new { x.FlowId, x.Output.TemplateJson });
        var uses = editable.Concat(live)
            .SelectMany(o => UsedBy(DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson)).Select(lookup => (Lookup: lookup, o.FlowId)))
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
