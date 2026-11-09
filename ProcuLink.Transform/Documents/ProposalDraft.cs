using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Documents;

/// <summary>Turns the AI's proposed output into a draft, keeping only what the backend can vouch for. Never throws.</summary>
public static class ProposalDraft
{
    private const int MaxNodes = 300, MaxDepth = 8, MaxName = 100, MaxQuote = 200;
    private static readonly string[] Formats = [FlowOutputFormats.Csv, FlowOutputFormats.Json, FlowOutputFormats.Xml, FlowOutputFormats.Fixed];
    private static readonly string[] AuthTypes = ["none", "apikey", "bearer", "basic", "oauth2_client_credentials"];

    private sealed class Walk
    {
        public int Count;
        public bool LeftOut;
        public bool HasArray;
        public readonly List<ProposedNode> Leaves = [];
    }

    public static (OutputDraft Draft, DraftCite? FormatCite, DraftDestination? Destination) From(ProposedOutput p, FlowFieldList fields, DocumentationText docs)
    {
        var format = (p?.Format ?? "").Trim().ToLowerInvariant();
        var name = (p?.Name ?? "").Trim();
        name = name.Length == 0 ? "Output" : name[..Math.Min(name.Length, MaxName)];
        try
        {
            return Build(p!, name, format, fields, docs);
        }
        catch
        {
            var empty = new OutputNodeTemplate { Format = FlowOutputFormats.TemplateFormat(format), Root = OutputNode.Obj("document") };
            return (new OutputDraft(name, format, null, empty, [], ["left_out"]), null, null);
        }
    }

    private static (OutputDraft, DraftCite?, DraftDestination?) Build(ProposedOutput p, string name, string format, FlowFieldList fields, DocumentationText docs)
    {
        var flat = format is FlowOutputFormats.Csv or FlowOutputFormats.Fixed;
        var w = new Walk();
        var kids = new List<OutputNode>();
        if (Formats.Contains(format)) kids = Kids(p.Root?.Children, format, false, 1, w);
        else w.LeftOut = true;
        var rootName = !flat && ValidName(p.Root?.Name) is { } n ? n : "document";
        var template = new OutputNodeTemplate
        {
            Format = FlowOutputFormats.TemplateFormat(format),
            Root = OutputNode.Obj(rootName, [.. kids]),
            CsvDialect = format == FlowOutputFormats.Csv && p.Delimiter is { Length: 1 } d ? new CsvDialect { Delimiter = d } : null,
        };

        var matches = OutputLeafPaths.Of(template)
            .Where(l => !string.IsNullOrWhiteSpace(w.Leaves[Idx(l.Node)].FieldKey))
            .Select(l => (new SlotMatch(l.Path, w.Leaves[Idx(l.Node)].FieldKey!.Trim(), 0), "docs")).ToList();
        var draft = DraftBinder.Apply(name, format, template, matches, fields);

        // Apply may move or copy leaves, so Required and Cite follow each leaf's own marker (its OutputPath), not its path.
        var final = OutputLeafPaths.Of(draft.Template);
        var leaves = draft.Leaves.Select((l, i) =>
        {
            var k = w.Leaves[Idx(final[i].Node)];
            return l with { Required = k.Required, Cite = CiteOf(k.Quote, docs) };
        }).ToList();
        IReadOnlyList<string> notes = w.LeftOut ? [.. draft.Notes, "left_out"] : draft.Notes;

        return (draft with { Template = draft.Template with { Root = Restore(draft.Template.Root) }, Leaves = leaves, Notes = notes },
            CiteOf(p.FormatQuote, docs), DestinationOf(p.Destination, docs));
    }

    private static List<OutputNode> Kids(IReadOnlyList<ProposedNode>? children, string format, bool inArray, int depth, Walk w)
    {
        var flat = format is FlowOutputFormats.Csv or FlowOutputFormats.Fixed;
        var seen = new HashSet<string>();
        var result = new List<OutputNode>();
        foreach (var k in children ?? [])
        {
            var kind = k?.Kind?.Trim().ToLowerInvariant();
            var name = ValidName(k?.Name);
            var allowed = flat ? kind == "field" || (kind == "array" && !inArray && !w.HasArray)
                : kind is "object" or "array" or "field" || (kind == "attribute" && format == FlowOutputFormats.Xml);
            if (k is null || name is null || !allowed || depth > MaxDepth || w.Count >= MaxNodes || !seen.Add(name))
            {
                w.LeftOut = true;
                continue;
            }
            w.Count++;
            if (kind is "field" or "attribute")
            {
                var rule = new OutputFieldRule
                {
                    OutputPath = $"#{w.Leaves.Count}",
                    Width = format == FlowOutputFormats.Fixed && k.Width is >= 1 and <= 1000 ? k.Width : null,
                };
                w.Leaves.Add(k);
                result.Add(kind == "field" ? OutputNode.FieldOf(name, rule) : OutputNode.Attr(name, rule));
            }
            else if (kind == "object") result.Add(OutputNode.Obj(name, [.. Kids(k.Children, format, inArray, depth + 1, w)]));
            else
            {
                w.HasArray = true;
                var items = Kids(k.Children, format, true, depth + 1, w);
                var item = items is [{ NodeType: OutputNodeType.Object } only] && !flat ? only : OutputNode.Obj(flat ? "row" : "item", [.. items]);
                result.Add(OutputNode.Arr(name, item));
            }
        }
        return result;
    }

    private static string? ValidName(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n.Length > MaxName || n.Contains('/') ? null : n;
    }

    private static int Idx(OutputNode leaf) => int.Parse(leaf.Rule!.OutputPath.AsSpan(1));

    private static OutputNode Restore(OutputNode n) => n.Rule is null
        ? n with { Children = n.Children.Select(Restore).ToList() }
        : n with { Rule = n.Rule with { OutputPath = n.Name } };

    private static DraftCite? CiteOf(string? quote, DocumentationText docs)
    {
        var q = (quote ?? "").Trim();
        q = q[..Math.Min(q.Length, MaxQuote)];
        return docs.Locate(q) is { } where ? new DraftCite(where, q) : null;
    }

    private static DraftDestination? DestinationOf(ProposedDestination? d, DocumentationText docs)
    {
        var method = d?.Method?.Trim().ToUpperInvariant();
        if (d is null || method is not ("POST" or "PUT" or "PATCH")
            || !Uri.TryCreate(d.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return null;
        var auth = d.AuthType?.Trim().ToLowerInvariant();
        return new DraftDestination(url.ToString(), method, AuthTypes.Contains(auth) ? auth! : "none", CiteOf(d.Quote, docs));
    }
}
