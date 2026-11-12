using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Documents;

public static class DraftBinder
{
    private static readonly string[] FlatFormats = [FlowOutputFormats.Csv, FlowOutputFormats.Xlsx, FlowOutputFormats.Fixed];

    public static IReadOnlyList<OutputSlot> Slots(OutputNodeTemplate t, string format, FlowFieldList fields)
    {
        var header = fields.Fields.Select(f => f.Key).ToList();
        var columns = fields.Tables.SelectMany(tb => tb.Columns.Select(c => $"{tb.Key}.{c.Key}")).ToList();
        var flat = FlatFormats.Contains(format);
        return OutputLeafPaths.Of(t)
            .Select(l => new OutputSlot(l.Path, l.Node.Name, l.InTable ? columns : flat ? [.. header, .. columns] : header))
            .ToList();
    }

    /// <summary>Matches a leaf's name to a field key or label, ignoring case and punctuation. Header fields win over columns.</summary>
    public static IReadOnlyList<SlotMatch> ByName(IReadOnlyList<OutputSlot> slots, FlowFieldList fields)
    {
        var matches = new List<SlotMatch>();
        foreach (var slot in slots)
        {
            var name = Plain(slot.Name);
            var key = fields.Fields.Where(f => slot.Allowed.Contains(f.Key) && (Plain(f.Key) == name || Plain(f.Label) == name)).Select(f => f.Key).FirstOrDefault()
                ?? fields.Tables.SelectMany(tb => tb.Columns.Select(c => (Q: $"{tb.Key}.{c.Key}", Col: c)))
                    .Where(x => slot.Allowed.Contains(x.Q) && (Plain(x.Col.Key) == name || Plain(x.Col.Label) == name)).Select(x => x.Q).FirstOrDefault();
            if (key is not null) matches.Add(new SlotMatch(slot.Path, key, 1f));
        }
        return matches;
    }

    /// <summary>
    /// Matches a leaf whose plain name is an alias of a meaning to the one field or column carrying that meaning.
    /// A meaning carried by several fields is ambiguous and binds nothing. Confidence 0.9.
    /// </summary>
    public static IReadOnlyList<SlotMatch> ByMeaning(IReadOnlyList<OutputSlot> slots, FlowFieldList fields)
    {
        var carriers = fields.Fields.Select(f => (Key: f.Key, f.Meaning))
            .Concat(fields.Tables.SelectMany(tb => tb.Columns.Select(c => (Key: $"{tb.Key}.{c.Key}", c.Meaning))))
            .Where(x => x.Meaning is not null).ToList();
        var matches = new List<SlotMatch>();
        foreach (var slot in slots)
        {
            var name = Plain(slot.Name);
            foreach (var meaning in FieldMeanings.Aliases.Where(a => a.Value.Contains(name)).Select(a => a.Key))
            {
                var keys = carriers.Where(x => x.Meaning == meaning && slot.Allowed.Contains(x.Key)).Select(x => x.Key).ToList();
                if (keys.Count != 1) continue;
                matches.Add(new SlotMatch(slot.Path, keys[0], 0.9f));
                break;
            }
        }
        return matches;
    }

    public static OutputDraft Apply(string name, string format, OutputNodeTemplate t, IReadOnlyList<(SlotMatch Match, string By)> matches, FlowFieldList fields)
    {
        var leaves = OutputLeafPaths.Of(t);
        var allowed = Slots(t, format, fields).ToDictionary(s => s.Path, s => s.Allowed);
        var chosen = new Dictionary<string, (SlotMatch Match, string By)>();
        foreach (var m in matches)
            if (allowed.TryGetValue(m.Match.Path, out var ok) && ok.Contains(m.Match.FieldKey)) chosen.TryAdd(m.Match.Path, m);

        var tableKey = leaves.Select(l => chosen.TryGetValue(l.Path, out var m) && IsColumn(m.Match.FieldKey) ? TableOf(m.Match.FieldKey) : null).FirstOrDefault(k => k is not null)
            ?? (fields.Tables.Count == 1 ? fields.Tables[0].Key : null);
        // One array holds one table's columns, so a column of another table is dropped.
        foreach (var p in chosen.Keys.ToList())
            if (IsColumn(chosen[p].Match.FieldKey) && TableOf(chosen[p].Match.FieldKey) != tableKey) chosen.Remove(p);

        var byLeaf = new Dictionary<OutputNode, string>(ReferenceEqualityComparer.Instance);
        foreach (var l in leaves) byLeaf[l.Node] = l.Path;
        var boundTo = new Dictionary<OutputNode, (SlotMatch Match, string By)>(ReferenceEqualityComparer.Instance);
        OutputNode Rewrite(OutputNode n)
        {
            if (byLeaf.TryGetValue(n, out var path) && n.Rule is not null && chosen.TryGetValue(path, out var m))
            {
                var bound = n with { Rule = n.Rule with { CanonicalField = Unqualified(m.Match.FieldKey) } };
                boundTo[bound] = m;
                return bound;
            }
            return n with
            {
                Children = n.Children.Select(Rewrite).ToList(),
                Collection = n.NodeType == OutputNodeType.Array ? tableKey : n.Collection,
            };
        }

        var root = Rewrite(t.Root);
        var notes = new List<string>();
        if (FlatFormats.Contains(format) && tableKey is not null)
        {
            var moved = t.Root.Children.Select(c => byLeaf.TryGetValue(c, out var p) && chosen.TryGetValue(p, out var m) && IsColumn(m.Match.FieldKey)).ToList();
            if (moved.Contains(true))
            {
                var kept = root.Children.Where((_, i) => !moved[i]).ToList();
                var row = OutputNode.Obj("row", root.Children.Where((_, i) => moved[i]).ToArray());
                if (moved.IndexOf(true) < moved.LastIndexOf(false)) notes.Add("moved_table_columns");
                root = root with { Children = [.. kept, OutputNode.Arr(tableKey, row, tableKey)] };
            }
        }

        var template = t with { Root = root };
        var draftLeaves = OutputLeafPaths.Of(template).Select(l => boundTo.TryGetValue(l.Node, out var m)
            ? new DraftLeaf(l.Path, Unqualified(m.Match.FieldKey), m.By, m.By == "docs" ? null : m.Match.Confidence)
            : new DraftLeaf(l.Path, null, null, null)).ToList();
        return new OutputDraft(name, format, tableKey, template, draftLeaves, notes);
    }

    private static string Plain(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static bool IsColumn(string key) => key.Contains('.');
    private static string TableOf(string key) => key[..key.IndexOf('.')];
    private static string Unqualified(string key) => key[(key.IndexOf('.') + 1)..];
}
