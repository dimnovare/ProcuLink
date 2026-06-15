using System.Text;
using System.Text.Json;
using System.Xml;
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
        // builder does. Line bags are built lazily per line inside an Array node.
        var headerRow = SourceMapReDerive.ApplyToHeaderRow(
            MappedTransformService.BuildHeaderRow(order, @override), @override, tokens);

        IReadOnlyDictionary<string, string> LineRowFor(PurchaseOrderLineEntity line) =>
            SourceMapReDerive.ApplyToLineRow(
                MappedTransformService.BuildLineRow(order, @override, line, catalogLookup), @override, tokens);

        var orderedLines = order.Lines.OrderBy(l => l.LineNumber).ToList();

        return template.Format switch
        {
            OutputFormat.Json =>
                Result(EmitJson(template, headerRow, LineRowFor, orderedLines), "application/json", ".json"),
            OutputFormat.Xml or OutputFormat.CXml or OutputFormat.Ubl =>
                Result(EmitXml(template, headerRow, LineRowFor, orderedLines), "application/xml", ".xml"),
            _ => throw new ArgumentException(
                     $"OutputTemplateEmitter does not yet support format '{template.Format}' (B3: JSON + XML).",
                     nameof(template)),
        };
    }

    // ── JSON ─────────────────────────────────────────────────────────────────────

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
                foreach (var child in node.Children)
                {
                    if (child.NodeType == OutputNodeType.Attribute) continue; // attributes are XML-only
                    w.WritePropertyName(child.Name);
                    WriteJsonValue(w, child, row, lineScope, lineRowFor, orderedLines);
                }
                w.WriteEndObject();
                break;

            case OutputNodeType.Array:
                w.WriteStartArray();
                var item = node.Children.FirstOrDefault();
                if (item is not null)
                    foreach (var line in orderedLines)
                        WriteJsonValue(w, item, lineRowFor(line), lineScope: true, lineRowFor, orderedLines);
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
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            WriteXmlNode(w, template.Root, headerRow, lineScope: false, lineRowFor, orderedLines, template.Namespaces);
            w.WriteEndDocument();
        }
        return buffer.ToArray();
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
                w.WriteStartElement(node.Name);
                if (rootNamespaces is not null)
                    foreach (var (prefix, uri) in rootNamespaces)
                        w.WriteAttributeString("xmlns", prefix, null, uri);
                // Attributes first, then element children.
                foreach (var attr in node.Children.Where(c => c.NodeType == OutputNodeType.Attribute))
                    w.WriteAttributeString(attr.Name, MappedTransformService.ResolveRule(attr.Rule ?? Empty, row, lineScope) ?? string.Empty);
                foreach (var child in node.Children.Where(c => c.NodeType != OutputNodeType.Attribute))
                    WriteXmlNode(w, child, row, lineScope, lineRowFor, orderedLines);
                w.WriteEndElement();
                break;

            case OutputNodeType.Array:
                // The Array node is a wrapper element; each line renders the item template inside it.
                w.WriteStartElement(node.Name);
                var item = node.Children.FirstOrDefault();
                if (item is not null)
                    foreach (var line in orderedLines)
                        WriteXmlNode(w, item, lineRowFor(line), lineScope: true, lineRowFor, orderedLines);
                w.WriteEndElement();
                break;

            default: // Field → <Name>value</Name>  (Attribute is handled by the parent Object)
                w.WriteStartElement(node.Name);
                w.WriteString(MappedTransformService.ResolveRule(node.Rule ?? Empty, row, lineScope) ?? string.Empty);
                w.WriteEndElement();
                break;
        }
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

    private static TransformResult Result(byte[] bytes, string contentType, string ext) =>
        new(new MemoryStream(bytes, writable: false), contentType, ext);
}
