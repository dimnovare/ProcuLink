using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Phase B — the format-aware emitter that renders an <see cref="OutputNodeTemplate"/> (a recursive
/// <see cref="OutputNode"/> tree) to bytes. Unlike <see cref="MappedTransformService"/> (which emits
/// a FLAT {header, lines} shape for CSV/JSON only), this walks an arbitrary tree, so a supplier's
/// exact required STRUCTURE — nesting, repeating groups, attributes, custom wrapper/root names — can
/// be produced for the structured formats too.
///
/// It REUSES <see cref="MappedTransformService"/>'s leaf-value resolution verbatim
/// (<c>BuildHeaderRow</c> / <c>BuildLineRow</c> / <c>ResolveRule</c> + the SourceMap re-derive), so
/// every leaf value is byte-identical to the flat builder — only the surrounding structure differs.
///
/// v1 (B3) implements the structured family: JSON + XML (the XML emitter is the base for cXML/UBL).
/// CSV (delimited) and X12 (segment) emitters + the per-format default templates follow in B4.
/// </summary>
public sealed class OutputTemplateEmitter
{
    /// <summary>
    /// Render <paramref name="template"/> against <paramref name="order"/> using the same value
    /// machinery as the flat builder. Throws <see cref="TransformValidationException"/> for an
    /// unresolved order (identical guard to the fixed/flat transforms — never deliver a blind doc).
    /// </summary>
    public TransformResult Emit(
        OutputNodeTemplate template,
        PurchaseOrderEntity order,
        OrderMappingOverride @override,
        IReadOnlyList<SourceToken>? sourceTokens = null,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null)
    {
        GuardResolved(order);

        var tokens = sourceTokens ?? Array.Empty<SourceToken>();

        // Header value bag (resolved once), with the SourceMap re-derive applied — exactly as the flat
        // builder does. Line bags are built lazily per line inside an Array node. F-1 Seam A: pass the
        // token list one level deeper so the row builders inject the reserved src:: keys.
        var headerRow = SourceMapReDerive.ApplyToHeaderRow(
            MappedTransformService.BuildHeaderRow(order, @override, tokens), @override, tokens);

        IReadOnlyDictionary<string, string> LineRowFor(PurchaseOrderLineEntity line) =>
            SourceMapReDerive.ApplyToLineRow(
                MappedTransformService.BuildLineRow(order, @override, line, catalogLookup, tokens), @override, tokens);

        var orderedLines = order.Lines.OrderBy(l => l.LineNumber).ToList();

        // ONE gate, asked of the SHARED predicate every caller uses to decide whether to route here at
        // all (OutputTreeFormats.IsRenderable). Spelling the refusal as its own format list is exactly
        // how the emitter and the "usable layout" predicate drifted: the predicate said "not cXML/X12"
        // while this switch also refused Ubl, UblOrder, X12_850 and EdifactOrders through its `_` arm,
        // so promoting a tree in one of those formats bricked every future order for the supplier.
        //
        // A non-renderable format needs the standard's own envelope/DOCTYPE (cXML's Header, Peppol's
        // UBLVersionID/CustomizationID/ProfileID, X12's ISA/GS, EDIFACT's UNB/UNH), none of which a
        // generic node tree carries. Emitting one anyway would deliver a well-formed document the
        // receiver REJECTS (offer ⇔ works). Those formats deliver through their dedicated transform.
        if (!OutputTreeFormats.IsRenderable(template.Format))
            throw new ArgumentException(
                $"The output-structure editor cannot produce valid {template.Format} (it needs the format's " +
                "envelope/DOCTYPE, not a generic element tree). This supplier delivers via the dedicated " +
                $"{template.Format} transform — design the output as generic XML/JSON/CSV, or remove the output tree.",
                nameof(template));

        return template.Format switch
        {
            OutputFormat.Json =>
                Result(EmitJson(template, headerRow, LineRowFor, orderedLines), "application/json", ".json"),
            OutputFormat.Xml =>
                Result(EmitXml(template, headerRow, LineRowFor, orderedLines), "application/xml", ".xml"),
            OutputFormat.Csv =>
                Result(EmitCsv(template, headerRow, LineRowFor, orderedLines), "text/csv", ".csv"),
            _ => throw new InvalidOperationException(
                     $"OutputTreeFormats.IsRenderable admitted '{template.Format}' but OutputTemplateEmitter has " +
                     "no arm for it. The predicate and the emitter are one decision — change them together."),
        };
    }

    // ── JSON ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The children a walk may safely dereference. <see cref="OutputNode.Children"/> is an
    /// <c>init</c> List with a default, but System.Text.Json overwrites the default with null for
    /// <c>"children": null</c> AND puts a null INTO the list for <c>"children": [null]</c> - both
    /// reachable from the <c>[FromBody] PoMappingConfig</c> PUT that stores the config verbatim. Every
    /// walk site goes through here so a hand-posted tree can never NullReference mid-document, after
    /// bytes have already been written. (<c>HasUsableOutputTree</c> keeps such a tree away from the
    /// emitter at the ROOT; a null nested deeper than the root passes that check.)
    /// </summary>
    private static IEnumerable<OutputNode> RealChildren(OutputNode node) =>
        node.Children?.Where(c => c is not null) ?? Enumerable.Empty<OutputNode>();

    private static byte[] EmitJson(
        OutputNodeTemplate template,
        IReadOnlyDictionary<string, string> headerRow,
        Func<PurchaseOrderLineEntity, IReadOnlyDictionary<string, string>> lineRowFor,
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteJsonValue(w, template.Root, headerRow, lineScope: false, lineRowFor, orderedLines);
        }
        return buffer.ToArray();
    }

    private static void WriteJsonValue(
        Utf8JsonWriter w, OutputNode node,
        IReadOnlyDictionary<string, string> row, bool lineScope,
        Func<PurchaseOrderLineEntity, IReadOnlyDictionary<string, string>> lineRowFor,
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines)
    {
        switch (node.NodeType)
        {
            case OutputNodeType.Object:
                w.WriteStartObject();
                foreach (var child in RealChildren(node))
                {
                    if (child.NodeType == OutputNodeType.Attribute) continue; // attributes are XML-only
                    if (!ShouldEmit(child, row, lineScope)) continue;         // conditional field
                    w.WritePropertyName(child.Name);
                    WriteJsonValue(w, child, row, lineScope, lineRowFor, orderedLines);
                }
                w.WriteEndObject();
                break;

            case OutputNodeType.Array:
                w.WriteStartArray();
                var item = RealChildren(node).FirstOrDefault();
                if (item is not null)
                    foreach (var line in orderedLines)
                    {
                        var lineRow = lineRowFor(line);
                        if (!ShouldEmit(item, lineRow, lineScope: true)) continue; // conditional line (e.g. skip qty 0)
                        WriteJsonValue(w, item, lineRow, lineScope: true, lineRowFor, orderedLines);
                    }
                w.WriteEndArray();
                break;

            default: // Field / Attribute → a string value
                w.WriteStringValue(MappedTransformService.ResolveRule(node.Rule ?? Empty, row, lineScope) ?? string.Empty);
                break;
        }
    }

    // ── XML (base for cXML / UBL) ──────────────────────────────────────────────────

    private static byte[] EmitXml(
        OutputNodeTemplate template,
        IReadOnlyDictionary<string, string> headerRow,
        Func<PurchaseOrderLineEntity, IReadOnlyDictionary<string, string>> lineRowFor,
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines)
    {
        // Two MUTUALLY-EXCLUSIVE namespace modes (mixing them double-declares / corrupts scope):
        //  • LEGACY root-map: template.Namespaces declares xmlns:* on the root; nodes are unprefixed.
        //    Kept byte-identical (the only mode the byte-parity oracle pins).
        //  • PER-NODE: nodes carry Namespace/Prefix; the prefixed namespaces are hoisted to the root
        //    once (so siblings don't each redeclare) and the writer binds each prefixed element.
        var hasRootMap = template.Namespaces is { Count: > 0 };
        var hasPerNode = AnyNamespacedNode(template.Root);
        if (hasRootMap && hasPerNode)
            throw new ArgumentException(
                "Use either root-level Namespaces OR per-node Prefix/Namespace, not both.", nameof(template));
        if (AnyPrefixWithoutNamespace(template.Root))
            throw new ArgumentException(
                "A node has a Prefix set without a Namespace URI; a prefix must be bound to a namespace " +
                "(set the node's Namespace, or use a root-level Namespaces map).", nameof(template));

        // The xmlns declarations to write on the root element. Legacy → the template map verbatim
        // (byte-identical). Per-node → the distinct prefixed namespaces collected from the tree (the
        // root's own default namespace is declared by the element itself, so it is excluded here).
        IReadOnlyDictionary<string, string>? rootDecls = hasRootMap
            ? template.Namespaces
            : hasPerNode ? CollectPrefixedNamespaces(template.Root) : null;

        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            WriteXmlNode(w, template.Root, headerRow, lineScope: false, lineRowFor, orderedLines, rootDecls);
            w.WriteEndDocument();
        }
        return buffer.ToArray();
    }

    /// <summary>Start <paramref name="node"/>'s element: prefix-bound, default-namespaced, or explicit NO-namespace (byte-identical when no ancestor default is in scope).</summary>
    private static void StartElement(XmlWriter w, OutputNode node)
    {
        if (node.Prefix is { Length: > 0 } p && node.Namespace is { Length: > 0 } pns)
            w.WriteStartElement(p, node.Name, pns);        // bound prefix; writer reuses the root-hoisted xmlns
        else if (node.Namespace is { Length: > 0 } dns)
            w.WriteStartElement(null, node.Name, dns);     // default namespace (no prefix)
        else
            // Explicit empty namespace so a no-namespace node NEVER silently inherits an ancestor's
            // default xmlns. XmlWriter omits a redundant xmlns="" when no default is in scope, so this
            // stays byte-identical to the legacy single-arg call for non-namespaced trees.
            w.WriteStartElement(null, node.Name, string.Empty);
    }

    /// <summary>A Prefix with no Namespace URI is an unrenderable half-state (the prefix would be silently dropped). Fail loud.</summary>
    private static bool AnyPrefixWithoutNamespace(OutputNode node) =>
        (node.Prefix is { Length: > 0 } && node.Namespace is not { Length: > 0 })
        || RealChildren(node).Any(AnyPrefixWithoutNamespace);

    private static bool AnyNamespacedNode(OutputNode node) =>
        node.Namespace is { Length: > 0 } || node.Prefix is { Length: > 0 }
        || RealChildren(node).Any(AnyNamespacedNode);

    private static IReadOnlyDictionary<string, string> CollectPrefixedNamespaces(OutputNode root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(OutputNode n)
        {
            if (n.Prefix is { Length: > 0 } p && n.Namespace is { Length: > 0 } ns)
                map[p] = ns;   // last write wins; a prefix maps to one URI per document (XML rule)
            foreach (var c in RealChildren(n)) Walk(c);
        }
        Walk(root);
        return map;
    }

    private static void WriteXmlNode(
        XmlWriter w, OutputNode node,
        IReadOnlyDictionary<string, string> row, bool lineScope,
        Func<PurchaseOrderLineEntity, IReadOnlyDictionary<string, string>> lineRowFor,
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines,
        IReadOnlyDictionary<string, string>? rootNamespaces = null)
    {
        switch (node.NodeType)
        {
            case OutputNodeType.Object:
                StartElement(w, node);
                if (rootNamespaces is not null)
                    foreach (var (prefix, uri) in rootNamespaces)
                        w.WriteAttributeString("xmlns", prefix, null, uri);
                // Attributes first, then element children. Conditional (IncludeWhen) nodes are skipped.
                foreach (var attr in RealChildren(node).Where(c => c.NodeType == OutputNodeType.Attribute))
                    if (ShouldEmit(attr, row, lineScope))
                        WriteXmlAttribute(w, attr, row, lineScope);
                foreach (var child in RealChildren(node).Where(c => c.NodeType != OutputNodeType.Attribute))
                    if (ShouldEmit(child, row, lineScope))
                        WriteXmlNode(w, child, row, lineScope, lineRowFor, orderedLines);
                w.WriteEndElement();
                break;

            case OutputNodeType.Array:
                // WRAPPED (named) → <Wrapper><item/>..</Wrapper>, e.g. <Lines><Line/>..</Lines>.
                // UNWRAPPED (empty name) → the item element repeats directly under the parent, e.g.
                // UBL <Order><cac:OrderLine/>..</Order> (no wrapper). Without this, inferring real UBL
                // (unwrapped repetition) would double-nest the line element.
                var wrapped = !string.IsNullOrWhiteSpace(node.Name);
                if (wrapped) StartElement(w, node);
                var item = RealChildren(node).FirstOrDefault();
                if (item is not null)
                    foreach (var line in orderedLines)
                    {
                        var lineRow = lineRowFor(line);
                        if (!ShouldEmit(item, lineRow, lineScope: true)) continue; // conditional line
                        WriteXmlNode(w, item, lineRow, lineScope: true, lineRowFor, orderedLines);
                    }
                if (wrapped) w.WriteEndElement();
                break;

            default: // Field → <Name>value</Name>  (Attribute is handled by the parent Object)
                StartElement(w, node);
                w.WriteString(MappedTransformService.ResolveRule(node.Rule ?? Empty, row, lineScope) ?? string.Empty);
                w.WriteEndElement();
                break;
        }
    }

    private static void WriteXmlAttribute(XmlWriter w, OutputNode attr, IReadOnlyDictionary<string, string> row, bool lineScope)
    {
        var value = MappedTransformService.ResolveRule(attr.Rule ?? Empty, row, lineScope) ?? string.Empty;
        if (attr.Prefix is { Length: > 0 } p && attr.Namespace is { Length: > 0 } ns)
            w.WriteAttributeString(p, attr.Name, ns, value);   // qualified attribute (e.g. xml:lang)
        else
            w.WriteAttributeString(attr.Name, value);          // LEGACY unqualified → byte-identical
    }

    // ── CSV (delimited) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Flatten the tree to a delimited grid, mirroring <see cref="MappedTransformService"/>'s flat CSV
    /// EXACTLY (same column order, escaping, line ordering, and AppendLine), so a converted flat config
    /// renders byte-identically. The root Object's Field children are the leading (header) columns,
    /// repeated on every row; the first Array child's item Object's Field children are the per-line
    /// columns. Column header = the node's Name; values resolve through the same machinery.
    /// </summary>
    private static byte[] EmitCsv(
        OutputNodeTemplate template,
        IReadOnlyDictionary<string, string> headerRow,
        Func<PurchaseOrderLineEntity, IReadOnlyDictionary<string, string>> lineRowFor,
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines)
    {
        var root = template.Root;
        var headerFields = RealChildren(root).Where(c => c.NodeType == OutputNodeType.Field).ToList();
        var arrayNode    = RealChildren(root).FirstOrDefault(c => c.NodeType == OutputNodeType.Array);
        var lineItem     = arrayNode is null ? null : RealChildren(arrayNode).FirstOrDefault();
        // `?:` binds LOOSER than `??`, so folding the empty-list default into the false arm made the
        // TRUE arm plain null — and a header-only CSV tree (no Array node at all) NullReferenced on the
        // very next line. Two explicit arms, no precedence to get wrong.
        var lineFields   = lineItem is null
                               ? new List<OutputNode>()
                               : RealChildren(lineItem).Where(c => c.NodeType == OutputNodeType.Field).ToList();

        var sb = new StringBuilder();

        var headerNames = headerFields.Select(f => f.Name).Concat(lineFields.Select(f => f.Name));
        sb.AppendLine(string.Join(",", headerNames.Select(MappedTransformService.Escape)));

        var headerValues = headerFields
            .Select(f => MappedTransformService.ResolveRule(f.Rule ?? Empty, headerRow, lineScope: false) ?? string.Empty)
            .ToList();

        foreach (var line in orderedLines)
        {
            var lineRow = lineRowFor(line);
            // CSV is a fixed column grid, so a conditional COLUMN can't vary per row; IncludeWhen on the
            // line-item template instead drops the whole ROW (e.g. skip zero-quantity lines).
            if (lineItem is not null && !ShouldEmit(lineItem, lineRow, lineScope: true)) continue;
            var lineValues = lineFields
                .Select(f => MappedTransformService.ResolveRule(f.Rule ?? Empty, lineRow, lineScope: true) ?? string.Empty);
            sb.AppendLine(string.Join(",", headerValues.Concat(lineValues).Select(MappedTransformService.Escape)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // ── Shared ─────────────────────────────────────────────────────────────────────

    /// <summary>Same unresolved-lines guard as the fixed/flat transforms — never emit a blind document.</summary>
    private static void GuardResolved(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();
        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    private static readonly OutputFieldRule Empty = new() { FixedValue = string.Empty };

    // ── Conditional inclusion (OutputNode.IncludeWhen) ─────────────────────────────

    /// <summary>
    /// Whether <paramref name="node"/> should be emitted, per its optional <see cref="OutputNode.IncludeWhen"/>
    /// predicate. Null/blank → always (byte-identical to before). Otherwise the bare condition is wrapped
    /// in <c>{{ ( … ) }}</c> and evaluated against the SAME row scope as a leaf <c>Expression</c>
    /// (<c>order.*</c>, plus <c>line.*</c> in line scope) — so <c>line.Quantity &gt; 0</c> renders
    /// "true"/"false". A predicate that fails to evaluate FAILS OPEN (kept): an authoring mistake must
    /// never silently drop the supplier's data.
    /// </summary>
    private static bool ShouldEmit(OutputNode node, IReadOnlyDictionary<string, string> row, bool lineScope)
    {
        var pred = node.IncludeWhen;
        if (string.IsNullOrWhiteSpace(pred)) return true;

        var wrapped = "{{ (" + pred + ") }}";
        var result = lineScope
            ? ScribanFieldEvaluator.EvaluateLine(wrapped, row)
            : ScribanFieldEvaluator.EvaluateHeader(wrapped, row);

        if (!result.Ok)
        {
            // Fail-open: our/author error never drops data silently (behaviour UNCHANGED). Log the
            // failure so a broken IncludeWhen predicate is observable instead of invisibly emitting
            // the node it was meant to gate.
            TransformDiagnostics.CreateLogger(nameof(OutputTemplateEmitter)).LogWarning(
                "IncludeWhen predicate \"{Predicate}\" failed to evaluate ({Scope}); failing open " +
                "(node emitted). Error: {Error}",
                pred, lineScope ? "line" : "header", result.Error);
            return true;
        }
        return IsTruthy(result.Value);
    }

    /// <summary>Scriban-rendered truthiness: blank / "false" / "0" / "no" / "off" / "null" → false; else true.</summary>
    private static bool IsTruthy(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        return !(t.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || t == "0"
                 || t.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || t.Equals("off", StringComparison.OrdinalIgnoreCase)
                 || t.Equals("null", StringComparison.OrdinalIgnoreCase));
    }

    private static TransformResult Result(byte[] bytes, string contentType, string ext) =>
        new(new MemoryStream(bytes, writable: false), contentType, ext);
}
