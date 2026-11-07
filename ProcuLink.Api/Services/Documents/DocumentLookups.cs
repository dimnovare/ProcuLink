using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Api.Services.Documents;

public static class DocumentLookups
{
    /// <summary>Ids of the lookups a template's Lookup steps point at; unparsable ids are skipped.</summary>
    public static IReadOnlySet<Guid> UsedBy(OutputNodeTemplate template)
    {
        var ids = new HashSet<Guid>();
        void Walk(OutputNode n)
        {
            foreach (var m in n.Rule?.FieldManipulators ?? [])
                if (m.Type == "Lookup" && m.Params.Count > 0 && Guid.TryParse(m.Params[0], out var id)) ids.Add(id);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(template.Root);
        return ids;
    }
}
