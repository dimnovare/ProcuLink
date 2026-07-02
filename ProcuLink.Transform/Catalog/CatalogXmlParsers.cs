using System.Xml;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Catalog;

/// <summary>
/// XML + CIF catalog parsers (plan 2026-07-02 D2/D3, Phase 3). Two XML strategies plus CIF:
///  • <see cref="ParseCxmlIndexAsync"/> — the cXML Index / catalog standard (bare
///    <c>&lt;Index&gt;</c> or SOAP-wrapped). Fixed, deterministic element→field mapping.
///  • <see cref="ParseGenericXmlAsync"/> — vendor XML (100MEGA <c>StoItem</c>, Jarltech
///    pricelist): finds the most frequent repeating element, flattens its scalar child
///    elements + attributes into a header/row table, then runs the SAME alias + per-source
///    mapping the CSV path uses.
///  • <see cref="ParseCifAsync"/> — Ariba CIF 3.0 text (header block → <c>FIELDNAMES:</c> →
///    <c>DATA</c> … <c>ENDOFDATA</c>, quoted-field aware).
///
/// STREAMING is load-bearing: 100MEGA's real StoItemBase feed is ~174 MB. Both XML parsers use
/// a forward-only <see cref="XmlReader"/> (never <c>XDocument</c>/<c>XmlDocument</c>, which would
/// materialize a multi-hundred-MB DOM) and skip large sub-trees they don't map. DTD/XXE is
/// disabled the same way as <see cref="DtdSafeXmlLoader"/> (DtdProcessing.Prohibit, no resolver).
/// </summary>
public static partial class SupplierCatalogFileParser
{
    /// <summary>
    /// Hardened reader settings — no DTD, no external entity resolution (XXE-safe). Synchronous
    /// (the catalog bytes are already buffered in memory by the pull pipeline / test), so we read
    /// with <see cref="XmlReader.Read"/> — mixing sync reads under <c>Async=true</c> throws.
    /// </summary>
    private static XmlReaderSettings SafeXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>
    /// XML router (plan 2026-07-02 4.2): a cXML <c>&lt;Index&gt;</c> anywhere under the root
    /// (covers the SOAP-wrapped case) → the dedicated cXML Index parser; otherwise the generic
    /// repeating-element parser. Detection reads only the element names up to the first
    /// <c>Index</c> / first non-root element, so it stays streaming-cheap even on the 174 MB feed.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseXmlAsync(
        Stream stream, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (!stream.CanSeek)
        {
            var buf = new MemoryStream();
            await stream.CopyToAsync(buf, ct);
            buf.Position = 0;
            stream = buf;
        }

        var isCxmlIndex = LooksLikeCxmlIndex(stream, ct);
        stream.Position = 0;

        return isCxmlIndex
            ? await ParseCxmlIndexAsync(stream, ct, overrides)
            : await ParseGenericXmlAsync(stream, ct, overrides);
    }

    /// <summary>
    /// True when an element named <c>Index</c> (by LocalName, any namespace) appears near the top
    /// of the document — bare or SOAP-wrapped. Scans at most the first ~50 element starts.
    /// </summary>
    private static bool LooksLikeCxmlIndex(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());
        var scanned = 0;
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (string.Equals(reader.LocalName, "Index", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (++scanned > 50) return false; // Index sits at/near the root; give up early
            }
        }
        catch (XmlException) { return false; }
        return false;
    }

    // ── cXML Index / catalog (bare + SOAP) ─────────────────────────────────────

    private static readonly string[] CxmlIndexHeaderColumns =
        { "SupplierPartID", "SupplierPartAuxiliaryID", "Description", "UnitOfMeasure", "UnitPrice", "currency", "ManufacturerPartID" };

    /// <summary>
    /// Parses a cXML Index: iterate <c>&lt;IndexItem&gt;</c> (by LocalName, namespace-agnostic),
    /// mapping <c>SupplierPartID→code</c>, <c>Description→name</c>, <c>UnitOfMeasure→unit</c>,
    /// <c>UnitPrice/Money→price</c> + <c>Money@currency→currency</c>,
    /// <c>SupplierPartAuxiliaryID→external_id</c>. Per-source <paramref name="overrides"/> can
    /// retarget any of the fixed source names (D3). Streaming, row-capped, <c>Format="cxml_index"</c>.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseCxmlIndexAsync(
        Stream stream, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var drafts = new List<SupplierProduct>();
        using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());

        var rows = 0;
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "IndexItem", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (++rows > MaxCatalogRows) throw new CatalogTooLargeException(RowCapMessage);

                var raw = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                ReadCxmlIndexItem(reader, raw, ct);

                // cXML Index is a fixed standard → dedicated source-name → canonical map (the
                // global CSV/CIF aliases don't cover PascalCase cXML names). Per-source overrides
                // still win (D3) so a non-standard feed can retarget any element.
                var fields = MapNamedRow(raw, MergeCxmlOverrides(overrides));
                var draft = RowToDraft(fields);
                if (draft is not null) drafts.Add(draft);
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Catalog XML could not be parsed.", ex);
        }

        return new CatalogFileParseResult(drafts, CxmlIndexHeaderColumns, "cxml_index");
    }

    /// <summary>
    /// Reads one <c>IndexItem</c> subtree into a raw name→value bag using canonical cXML source
    /// names. Uses <see cref="XmlReader.ReadSubtree"/> so it iterates the item's descendants in
    /// isolation (no fragile depth math against the shared reader) and leaves the outer reader
    /// positioned on the item's end tag afterward.
    /// </summary>
    private static void ReadCxmlIndexItem(XmlReader outer, IDictionary<string, string?> raw, CancellationToken ct)
    {
        if (outer.IsEmptyElement) return;
        using var reader = outer.ReadSubtree();
        reader.Read(); // advance onto the IndexItem element itself

        // ReadElementContentAsString advances PAST the element's end tag, so after a matched case
        // the reader is already on the next node — do NOT Read() again before inspecting it, or
        // sibling elements (e.g. SupplierPartAuxiliaryID after SupplierPartID) get skipped.
        var moved = reader.Read();
        while (moved)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                moved = reader.Read();
                continue;
            }

            var consumed = false;
            switch (reader.LocalName)
            {
                case "SupplierPartID":
                    raw["SupplierPartID"] = reader.ReadElementContentAsString().Trim(); consumed = true;
                    break;
                case "SupplierPartAuxiliaryID":
                    raw["SupplierPartAuxiliaryID"] = reader.ReadElementContentAsString().Trim(); consumed = true;
                    break;
                case "Description":
                    // First (typically English) description wins.
                    if (!raw.ContainsKey("Description"))
                        raw["Description"] = reader.ReadElementContentAsString().Trim();
                    else reader.Skip();
                    consumed = true;
                    break;
                case "UnitOfMeasure":
                    raw["UnitOfMeasure"] = reader.ReadElementContentAsString().Trim(); consumed = true;
                    break;
                case "ManufacturerPartID":
                    raw["ManufacturerPartID"] = reader.ReadElementContentAsString().Trim(); consumed = true;
                    break;
                case "Money":
                    // <UnitPrice><Money currency="EUR">7.01</Money></UnitPrice>
                    var currency = reader.GetAttribute("currency");
                    if (!string.IsNullOrWhiteSpace(currency)) raw["currency"] = currency.Trim();
                    raw["UnitPrice"] = reader.ReadElementContentAsString().Trim(); consumed = true;
                    break;
            }

            // Only advance when the case did NOT already consume+advance the reader.
            moved = consumed ? !reader.EOF : reader.Read();
        }
    }

    // ── Generic repeating-element XML (100MEGA StoItem, Jarltech) ───────────────

    /// <summary>
    /// Generic vendor-XML parser (D2). Two passes over the (buffered) stream:
    ///  1. find the most frequent repeating element name (≥2 occurrences) — the "row" element;
    ///  2. for each occurrence flatten its ATTRIBUTES + scalar CHILD elements into a name→value
    ///     row, union the names as the header, then run the SAME alias + per-source override
    ///     mapping the CSV path uses.
    /// Streaming (XmlReader), row-capped, skips non-scalar children (nested trees like 100MEGA's
    /// <c>&lt;ImgGal&gt;</c>). <c>Format="xml"</c>. Honest by construction: the test-fetch report
    /// shows exactly which flattened columns mapped; an un-flattenable feed yields 0 rows + the
    /// detected columns, never silent garbage.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseGenericXmlAsync(
        Stream stream, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (!stream.CanSeek)
        {
            var buf = new MemoryStream();
            await stream.CopyToAsync(buf, ct);
            buf.Position = 0;
            stream = buf;
        }

        var rowElement = FindRepeatingElement(stream, ct);
        stream.Position = 0;
        if (rowElement is null)
            return new CatalogFileParseResult(new List<SupplierProduct>(), Array.Empty<string>(), "xml");

        var drafts = new List<SupplierProduct>();
        var headerOrder = new List<string>();
        var headerSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = 0;

        using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, rowElement, StringComparison.Ordinal))
                    continue;

                if (++rows > MaxCatalogRows) throw new CatalogTooLargeException(RowCapMessage);

                var raw = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                FlattenElement(reader, raw, headerOrder, headerSeen, ct);

                var fields = MapNamedRow(raw, overrides);
                var draft = RowToDraft(fields);
                if (draft is not null) drafts.Add(draft);
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Catalog XML could not be parsed.", ex);
        }

        return new CatalogFileParseResult(drafts, headerOrder, "xml");
    }

    /// <summary>
    /// First pass: the "row" element name. Counted per DEPTH, and the winner is the most frequent
    /// element (≥2 occurrences) at the SHALLOWEST depth that has a repeating element — i.e. the
    /// record rows, not their nested children. This is what makes 100MEGA work: <c>StoItem</c> is a
    /// direct child of the root <c>&lt;Result&gt;</c>, whereas the far-more-numerous <c>ImgGal</c> /
    /// category nodes are nested deeper, so a naive "globally most frequent" would pick the wrong
    /// element. Scans a bounded number of element starts so a pathological document can't make this
    /// pass unbounded.
    /// </summary>
    private static string? FindRepeatingElement(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, SafeXmlReaderSettings());
        // depth → (elementName → count)
        var byDepth = new Dictionary<int, Dictionary<string, int>>();
        var scanned = 0;
        const int scanCap = 2_000_000; // element-start budget for the detection pass
        try
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                var name = reader.LocalName;
                var depth = reader.Depth;
                if (!byDepth.TryGetValue(depth, out var counts))
                    byDepth[depth] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
                counts.TryGetValue(name, out var c);
                counts[name] = c + 1;
                if (++scanned > scanCap) break;
            }
        }
        catch (XmlException) { return null; }

        // Walk depths shallow→deep; take the most frequent repeating element at the first depth
        // that has one. Record rows live at the shallowest repeating depth; nested children
        // (ImgGal, category trees) are deeper and must not win even though they're more numerous.
        foreach (var depth in byDepth.Keys.OrderBy(d => d))
        {
            var best = byDepth[depth]
                .Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Select(kv => (string?)kv.Key)
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>
    /// Flattens one row element into name→value: all ATTRIBUTES, plus each SCALAR child element's
    /// text (children with their own element children — nested trees like <c>ImgGal</c> — are
    /// skipped, not recursed). First value for a name wins. Header names are collected in
    /// first-seen order for the honesty report.
    /// </summary>
    private static void FlattenElement(
        XmlReader outer, IDictionary<string, string?> raw,
        List<string> headerOrder, HashSet<string> headerSeen, CancellationToken ct)
    {
        // Attributes of the row element (captured before we descend into a subtree reader).
        if (outer.HasAttributes)
        {
            for (var i = 0; i < outer.AttributeCount; i++)
            {
                outer.MoveToAttribute(i);
                var an = outer.LocalName;
                if (headerSeen.Add(an)) headerOrder.Add(an);
                if (!raw.ContainsKey(an)) raw[an] = outer.Value?.Trim();
            }
            outer.MoveToElement();
        }

        if (outer.IsEmptyElement) return;

        // Isolate the row's subtree so ReadElementContentAsString on scalar children never
        // desyncs the shared reader. rootDepth+1 children only; nested trees (100MEGA <ImgGal>)
        // are skipped via ReadInnerXml (advances past the whole subtree).
        using var reader = outer.ReadSubtree();
        reader.Read(); // onto the row element itself
        var rootDepth = reader.Depth;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth != rootDepth + 1) continue; // direct children only

            var childName = reader.LocalName;
            if (reader.IsEmptyElement)
            {
                if (headerSeen.Add(childName)) headerOrder.Add(childName);
                continue;
            }

            // A scalar child has only text content. ReadElementContentAsString throws on
            // element/mixed content — treat that as a nested tree and skip it via ReadInnerXml.
            try
            {
                var text = reader.ReadElementContentAsString();
                if (headerSeen.Add(childName)) headerOrder.Add(childName);
                if (!raw.ContainsKey(childName)) raw[childName] = text.Trim();
            }
            catch (XmlException)
            {
                if (headerSeen.Add(childName)) headerOrder.Add(childName);
                reader.Skip(); // past the non-scalar subtree, staying streaming
            }
        }
    }

    /// <summary>
    /// Maps a raw name→value row through per-source overrides (win) then global aliases, to the
    /// canonical <c>code/name/unit/price/currency/barcode/external_id</c> bag <see cref="RowToDraft"/>
    /// consumes. <c>currency</c> passes through unchanged when already canonical.
    /// </summary>
    private static Dictionary<string, string?> MapNamedRow(
        IDictionary<string, string?> raw, IReadOnlyDictionary<string, string>? overrides)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Overrides win outright — apply them first so an explicit mapping (e.g. EAN→barcode) can
        // never be pre-empted by a global alias on another column (e.g. Code2→barcode).
        if (overrides is not null)
        {
            foreach (var (name, target) in overrides)
            {
                if (!CanonicalFields.Contains(target)) continue;
                if (raw.TryGetValue(name, out var v) && !fields.ContainsKey(target))
                    fields[target] = v;
            }
        }

        foreach (var (name, value) in raw)
        {
            if (overrides is not null && overrides.ContainsKey(name)) continue; // already applied
            string? canonical = null;
            if (CatalogColumnAliases.TryGetValue(name, out var aliased))
                canonical = aliased;
            else if (CanonicalFields.Contains(name)) // already-canonical source name (e.g. "currency")
                canonical = name;

            if (canonical is not null && !fields.ContainsKey(canonical))
                fields[canonical] = value;
        }
        return fields;
    }

    /// <summary>Fixed cXML Index source-element → canonical field map (the standard's shape).</summary>
    private static readonly Dictionary<string, string> CxmlIndexFieldMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SupplierPartID"] = "code",
            ["Description"] = "name",
            ["UnitOfMeasure"] = "unit",
            ["UnitPrice"] = "price",
            ["SupplierPartAuxiliaryID"] = "external_id",
            // "currency" is already stored canonical (from Money@currency).
        };

    /// <summary>
    /// Merges the fixed cXML Index field map with any per-source overrides (overrides win).
    /// </summary>
    private static Dictionary<string, string> MergeCxmlOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(CxmlIndexFieldMap, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
            foreach (var (k, v) in overrides)
                merged[k] = v;
        return merged;
    }

    // ── CIF 3.0 (Ariba catalog interchange format) ─────────────────────────────

    /// <summary>
    /// Parses an Ariba CIF 3.0 text file (plan 2026-07-02 3.4). Reads the header block until the
    /// lone <c>DATA</c> line: <c>FIELDNAMES:</c> drives the column names (aliased like a CSV
    /// header — CIF's spaced names "Supplier Part ID"/"Unit Price"/… are in the global alias
    /// table), and <c>CURRENCY:</c> supplies a per-row default when no currency column maps. Data
    /// rows are comma-delimited with quoted fields (descriptions embed commas) via
    /// <see cref="SplitDelimitedLine"/>; parsing stops at <c>ENDOFDATA</c>. Row-capped,
    /// <c>Format="cif"</c>.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseCifAsync(
        Stream stream, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var drafts = new List<SupplierProduct>();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        List<string> header = new();
        string? defaultCurrency = null;
        var inData = false;
        Dictionary<int, string> colMap = new();
        var rows = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (!inData)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("CURRENCY:", StringComparison.OrdinalIgnoreCase))
                    defaultCurrency = trimmed.Substring("CURRENCY:".Length).Trim();
                else if (trimmed.StartsWith("FIELDNAMES:", StringComparison.OrdinalIgnoreCase))
                    header = SplitDelimitedLine(trimmed.Substring("FIELDNAMES:".Length).TrimStart(), ',')
                             .Select(h => h.Trim().Trim('"')).ToList();
                else if (string.Equals(trimmed, "DATA", StringComparison.OrdinalIgnoreCase))
                {
                    inData = true;
                    colMap = MapHeaderColumns(header, overrides);
                    if (!colMap.ContainsValue("code"))
                        return new CatalogFileParseResult(drafts, header, "cif"); // unusable
                }
                continue;
            }

            if (string.Equals(line.Trim(), "ENDOFDATA", StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (++rows > MaxCatalogRows) throw new CatalogTooLargeException(RowCapMessage);

            var parts = SplitDelimitedLine(line, ',');
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (idx, canonical) in colMap)
                fields[canonical] = idx < parts.Count ? parts[idx].Trim().Trim('"') : null;

            // CURRENCY: header supplies the row currency when no currency column mapped.
            if (defaultCurrency is not null && !fields.ContainsKey("currency"))
                fields["currency"] = defaultCurrency;

            var draft = RowToDraft(fields);
            if (draft is not null) drafts.Add(draft);
        }

        return new CatalogFileParseResult(drafts, header, "cif");
    }
}
