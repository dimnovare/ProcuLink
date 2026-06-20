using System.Text.Json;
using System.Xml.Linq;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Phase D — infer an <see cref="OutputNodeTemplate"/> from a SAMPLE of the file a supplier requires.
/// The single most direct answer to "make this exact file": the user pastes (or uploads) the
/// supplier's required sample, and the designer opens already shaped to match.
///
/// DETERMINISTIC — no AI, no network. Parses the sample's STRUCTURE (nesting, repeating groups,
/// attributes, column names) into the node tree, and uses a name heuristic to pre-bind each leaf to
/// the most likely canonical field (the user adjusts in the designer). Because it is deterministic it
/// works for no-egress orgs too. JSON + CSV in v1; XML is a follow-up.
/// </summary>
public static class OutputNodeTemplateInferrer
{
    public static OutputNodeTemplate FromSample(string sample, OutputFormat format)
    {
        if (string.IsNullOrWhiteSpace(sample))
            throw new ArgumentException("Sample is empty.", nameof(sample));

        return format switch
        {
            OutputFormat.Json => FromJson(sample),
            OutputFormat.Csv  => FromCsv(sample),
            OutputFormat.Xml or OutputFormat.CXml or OutputFormat.Ubl => FromXml(sample, format),
            _ => throw new ArgumentException(
                     $"Sample inference supports JSON, CSV, and XML (got '{format}').", nameof(format)),
        };
    }

    // ── XML (cXML / UBL share the parser) ────────────────────────────────────────

    private static OutputNodeTemplate FromXml(string sample, OutputFormat format)
    {
        var doc = XDocument.Parse(sample);
        var root = NodeFromXml(doc.Root ?? throw new ArgumentException("XML sample has no root element.", nameof(sample)), lineScope: false);
        return new OutputNodeTemplate { Format = format, Root = root };
    }

    private static OutputNode NodeFromXml(XElement el, bool lineScope)
    {
        var (ns, prefix) = NsOf(el.Name, el);

        var attrs = el.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .Select(a =>
            {
                var (ans, aprefix) = NsOf(a.Name, el);
                return OutputNode.Attr(a.Name.LocalName, RuleFor(a.Name.LocalName, lineScope))
                    with { Namespace = ans, Prefix = aprefix };
            })
            .ToList();

        var childElements = el.Elements().ToList();
        if (childElements.Count == 0)
        {
            // A leaf element with attributes is modelled as an object carrying them; a pure leaf is a field.
            return attrs.Count > 0
                ? new OutputNode { Name = el.Name.LocalName, NodeType = OutputNodeType.Object, Namespace = ns, Prefix = prefix, Children = attrs }
                : OutputNode.FieldOf(el.Name.LocalName, RuleFor(el.Name.LocalName, lineScope)) with { Namespace = ns, Prefix = prefix };
        }

        // Group by the QUALIFIED name (namespace-aware) so cac:Foo and cbc:Foo never collide.
        var groups = childElements.GroupBy(c => c.Name).ToList();

        // Wrapped repeating group (e.g. <Lines><Line/><Line/></Lines>) → a NAMED array wrapper.
        if (attrs.Count == 0 && groups.Count == 1 && groups[0].Count() > 1)
        {
            return new OutputNode
            {
                Name = el.Name.LocalName, NodeType = OutputNodeType.Array, Namespace = ns, Prefix = prefix, Collection = "lines",
                Children = new() { NodeFromXml(groups[0].First(), lineScope: true) },
            };
        }

        // Object: attributes first, then the children. CRITICAL: only ONE repeated group is the
        // per-line array (its item repeats once per ORDER LINE). Every OTHER repeated sibling (e.g.
        // multiple header <cbc:Note>, <cac:AdditionalDocumentReference>) must be preserved as its OWN
        // occurrences — NOT collapsed into a second "lines" array, which would fan it out per order
        // line and drop the header content. Pick the line group by name heuristic, else the
        // last-positioned repeat (UBL emits cac:OrderLine after the header repeats).
        var repeated = groups.Where(g => g.Count() > 1).ToList();
        var lineGroup = repeated.Count == 0 ? null
            : repeated.FirstOrDefault(g => IsLikelyLineName(g.Key.LocalName)) ?? repeated[^1];

        var children = new List<OutputNode>(attrs);
        foreach (var g in groups)
        {
            if (ReferenceEquals(g, lineGroup))
                // The single per-line group → UNWRAPPED array (empty name); item carries the namespace.
                children.Add(new OutputNode { Name = "", NodeType = OutputNodeType.Array, Collection = "lines",
                    Children = new() { NodeFromXml(g.First(), lineScope: true) } });
            else if (g.Count() > 1)
                // Other repeated siblings → one node PER occurrence (header repeats, emitted as-is).
                children.AddRange(g.Select(occ => NodeFromXml(occ, lineScope)));
            else
                children.Add(NodeFromXml(g.First(), lineScope));
        }
        return new OutputNode { Name = el.Name.LocalName, NodeType = OutputNodeType.Object, Namespace = ns, Prefix = prefix, Children = children };
    }

    /// <summary>True if a repeated element's local name reads like the per-line group (UBL OrderLine, cXML ItemOut, generic line/item/detail).</summary>
    private static bool IsLikelyLineName(string localName)
    {
        var n = localName.ToLowerInvariant();
        return n.Contains("line") || n.Contains("item") || n.Contains("detail")
            || n.Contains("orderline") || n.Contains("lineitem");
    }

    /// <summary>The (namespace URI, prefix) of an XML name. Null/null for the no-namespace case (legacy byte-identical). Prefix null = a default namespace.</summary>
    private static (string? Namespace, string? Prefix) NsOf(System.Xml.Linq.XName name, XElement scope)
    {
        var ns = name.NamespaceName;
        if (string.IsNullOrEmpty(ns)) return (null, null);
        return (ns, scope.GetPrefixOfNamespace(name.Namespace));
    }

    // ── JSON ─────────────────────────────────────────────────────────────────────

    private static OutputNodeTemplate FromJson(string sample)
    {
        using var doc = JsonDocument.Parse(sample);
        var root = NodeFromJson("root", doc.RootElement, lineScope: false);
        return new OutputNodeTemplate { Format = OutputFormat.Json, Root = root };
    }

    private static OutputNode NodeFromJson(string name, JsonElement el, bool lineScope)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var children = el.EnumerateObject()
                    .Select(p => NodeFromJson(p.Name, p.Value, lineScope))
                    .ToList();
                return new OutputNode { Name = name, NodeType = OutputNodeType.Object, Children = children };

            case JsonValueKind.Array:
                // A JSON array is the repeating line group. Shape the item from the first element.
                var first = el.EnumerateArray().FirstOrDefault();
                var item = first.ValueKind == JsonValueKind.Undefined
                    ? new OutputNode { Name = "item", NodeType = OutputNodeType.Object }
                    : NodeFromJson("item", first, lineScope: true);
                return new OutputNode
                {
                    Name = name, NodeType = OutputNodeType.Array, Collection = "lines",
                    Children = new() { item },
                };

            default: // string / number / bool / null → a leaf value
                return OutputNode.FieldOf(name, RuleFor(name, lineScope));
        }
    }

    // ── CSV ──────────────────────────────────────────────────────────────────────

    private static OutputNodeTemplate FromCsv(string sample)
    {
        var firstLine = sample
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?? throw new ArgumentException("CSV sample has no header row.", nameof(sample));

        var columns = SplitCsvHeader(firstLine);
        // Treat each column as a per-line field (the common one-row-per-line case); the emitter repeats
        // header-level values per row anyway, so this round-trips a flat CSV.
        var lineFields = columns
            .Select(c => OutputNode.FieldOf(c, RuleFor(c, lineScope: true)))
            .ToArray();

        var root = OutputNode.Obj("root",
            OutputNode.Arr("lines", OutputNode.Obj("line", lineFields)));
        return new OutputNodeTemplate { Format = OutputFormat.Csv, Root = root };
    }

    private static List<string> SplitCsvHeader(string line)
    {
        // Minimal RFC-4180-aware header split (handles quoted commas in column names).
        var cols = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { cols.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        cols.Add(sb.ToString().Trim());
        return cols.Select((c, i) => string.IsNullOrWhiteSpace(c) ? $"column{i + 1}" : c).ToList();
    }

    // ── Name → canonical-field heuristic ─────────────────────────────────────────

    /// <summary>
    /// Build a leaf rule: pre-bind to the most likely canonical field by name; else leave the leaf
    /// UNBOUND for the user to map. Pure name matching — never guesses values.
    ///
    /// F-2: an unmappable column emits <see cref="OutputFieldRule.FixedValue"/> = <c>null</c> (UNBOUND),
    /// NOT <c>""</c>. A null fixed value + null canonical field is what the designer reads as "needs a
    /// field" (amber), so an inferred-but-unmapped column surfaces as TODO instead of masquerading as a
    /// bound empty-string pill that silently delivers "" forever. This is purely a binding-state change:
    /// at DELIVERY time an unbound leaf and an empty-fixed-value leaf BOTH resolve to "" (the emitters
    /// coalesce a null resolved value to string.Empty), so emitted bytes are unchanged. An explicitly
    /// user-set "" still travels as FixedValue = "" and still delivers "".
    /// </summary>
    private static OutputFieldRule RuleFor(string name, bool lineScope)
    {
        var canonical = GuessCanonical(name, lineScope);
        return canonical is null
            ? new OutputFieldRule { OutputPath = name, FixedValue = null }
            : new OutputFieldRule { OutputPath = name, CanonicalField = canonical };
    }

    private static string? GuessCanonical(string rawName, bool lineScope)
    {
        var n = new string(rawName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (n.Length == 0) return null;

        bool Has(params string[] needles) => needles.Any(needle => n.Contains(needle));

        if (lineScope)
        {
            if (Has("suppliercode", "supplieritem", "vendoritem", "sku", "itemcode", "partnumber", "partno", "mpn")) return "SupplierItemCode";
            if (Has("buyeritem", "buyercode", "customeritem")) return "BuyerItemCode";
            if (Has("manufacturerpart", "mfgpart")) return "ManufacturerPartNumber";
            if (Has("quantity", "qty")) return "Quantity";
            if (Has("unitprice", "price", "cost", "unitcost")) return "UnitPrice";
            if (Has("linetotal", "lineamount", "amount", "extended")) return "LineAmount";
            if (Has("description", "desc", "name", "title")) return "Description";
            if (Has("linenumber", "lineno", "linenum", "position", "pos")) return "LineNumber";
            if (Has("unit", "uom")) return "Unit";
            // No early return: fall through to header guesses — a per-line column can carry a header
            // value (e.g. a CSV "OrderRef" column repeated on every row binds to PoNumber).
        }

        if (Has("ponumber", "ordernumber", "ordernr", "orderref", "purchaseorder", "ordno", "ponum", "poref", "pono", "ponr", "ordernum", "orderno")) return "PoNumber";
        if (Has("orderdate", "podate", "date")) return "OrderDate";
        if (Has("currency", "curr", "ccy")) return "Currency";
        if (Has("buyername", "buyer", "customer", "company")) return "BuyerName";
        if (Has("suppliername", "supplier", "vendor", "seller")) return "SupplierName";
        return null;
    }
}
