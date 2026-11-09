using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Api.Services.Documents;

public static class DocumentLookups
{
    /// <summary>Every Lookup step in a template, whatever its params.</summary>
    public static IEnumerable<ManipulatorEntry> Steps(OutputNodeTemplate template)
    {
        IEnumerable<ManipulatorEntry> Walk(OutputNode n) =>
            (n.Rule?.FieldManipulators ?? []).Where(m => m?.Type == "Lookup").Concat((n.Children ?? []).SelectMany(Walk));
        return template.Root is null ? [] : Walk(template.Root);
    }

    /// <summary>Ids of the lookups a template's Lookup steps point at; unparsable ids are skipped.</summary>
    public static IReadOnlySet<Guid> UsedBy(OutputNodeTemplate template) =>
        Steps(template).Select(m => Guid.TryParse((m.Params ?? []).ElementAtOrDefault(0), out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet();

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
