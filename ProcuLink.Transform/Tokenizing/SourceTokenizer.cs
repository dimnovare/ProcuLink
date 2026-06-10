using System.Text;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace ProcuLink.Transform.Tokenizing;

/// <summary>
/// Concrete implementation of <see cref="ISourceTokenizer"/> covering CSV, XML, XLSX, EDIFACT,
/// X12, and XML-family (cXML, UBL, IDoc ORDERS05) source files.
///
/// <list type="bullet">
///   <item>
///     <b>CSV</b> — every cell in every row (including the header row) is emitted as a token
///     with id <c>"cell:r{row}c{col}"</c> (1-based). The header row cell labels are included
///     in the human-readable label. Supports comma- and semicolon-delimited files (same delimiter
///     detection as <c>CsvOrderParser</c>).
///   </item>
///   <item>
///     <b>XLSX</b> — every non-empty cell in the first worksheet is emitted with id
///     <c>"cell:r{row}c{col}"</c> (1-based, Excel row/column numbers). Row 1 is treated as the
///     header row (Group="header"); rows 2+ are "line". The token scheme is intentionally
///     identical to CSV so saved <c>SourceFieldRule.SourceToken</c> values are
///     format-independent.
///   </item>
///   <item>
///     <b>XML / cXML / UBL / IDoc ORDERS05</b> — every leaf element text node and every
///     attribute value is emitted. The id is the XPath from the document root, with 1-based
///     position predicates on repeated element names, e.g. <c>"/Order/Lines/Line[2]/Qty"</c>.
///     The same path covers bare-element, namespaced, and XML-family formats because all
///     parsers use local-name comparison. The cXML extension <c>.cxml</c> is routed here
///     alongside <c>.xml</c>.
///   </item>
///   <item>
///     <b>EDIFACT (<c>.edi</c>)</b> — every non-empty element and component value is emitted.
///     Token id: <c>"seg:{TAG}[{n}].el{element}[.c{component}]"</c> where <c>n</c> is the
///     1-based occurrence count of that tag in the message, element is 1-based, and the
///     optional <c>.c{component}</c> suffix is appended when the element has more than one
///     component. Header segments (before the first LIN) carry Group="header"; segments
///     from the first LIN onward carry Group="line".
///   </item>
///   <item>
///     <b>X12 (<c>.x12</c>)</b> — same scheme as EDIFACT. Token id:
///     <c>"seg:{TAG}[{n}].el{element}"</c>. Header segments (before the first PO1) carry
///     Group="header"; segments from the first PO1 onward carry Group="line".
///   </item>
///   <item>
///     <b>PDF</b> — not tokenised (no stable addressable cells in a rendered PDF). Returns
///     an empty list so downstream code falls through to the existing parsed value.
///   </item>
///   <item>
///     <b>All other formats</b> — returns an empty list (tokenisation not yet implemented).
///     This is a deliberate no-op: the engine never throws for an unsupported format; the
///     SourceMap simply falls through to the existing parsed value for any field whose
///     SourceToken id cannot be resolved.
///   </item>
/// </list>
/// </summary>
public sealed class SourceTokenizer : ISourceTokenizer
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<SourceToken>> TokenizeAsync(
        byte[] sourceBytes,
        string fileExtension,
        CancellationToken ct = default)
    {
        var ext = (fileExtension ?? string.Empty).Trim().ToLowerInvariant();

        IReadOnlyList<SourceToken> result = ext switch
        {
            ".csv"  => TokenizeCsv(sourceBytes),
            ".xlsx" => TokenizeXlsx(sourceBytes),
            ".xml"  => TokenizeXml(sourceBytes),
            ".cxml" => TokenizeXml(sourceBytes),  // cXML is XML; reuse the generic XML walk
            ".edi"  => TokenizeEdifact(sourceBytes),
            ".x12"  => TokenizeX12(sourceBytes),
            // PDF — no stable addressable cells in a rendered PDF; always return empty.
            // All other unrecognised formats — return empty so downstream falls through.
            _ => Array.Empty<SourceToken>(),
        };

        return Task.FromResult(result);
    }

    // ── XLSX ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenises an XLSX workbook (first worksheet only) using ClosedXML.
    /// Every non-empty cell is emitted with id <c>"cell:r{row}c{col}"</c> (1-based,
    /// matching the Excel row/column numbers). Row 1 is treated as the header row
    /// (Group="header"); rows 2+ are "line". The token scheme is identical to the
    /// CSV scheme so saved rules work across both formats without change.
    /// </summary>
    private static IReadOnlyList<SourceToken> TokenizeXlsx(byte[] bytes)
    {
        try
        {
            using var ms       = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(ms);

            var worksheet = workbook.Worksheets.FirstOrDefault();
            if (worksheet is null) return Array.Empty<SourceToken>();

            var rangeUsed = worksheet.RangeUsed();
            if (rangeUsed is null) return Array.Empty<SourceToken>();

            var tokens  = new List<SourceToken>();
            var headers = new List<string>(); // column header labels (from row 1)

            foreach (var row in rangeUsed.RowsUsed())
            {
                var rowNum = row.RowNumber();   // 1-based Excel row index

                // Build header cache from the first row.
                if (rowNum == 1)
                {
                    foreach (var cell in row.Cells())
                    {
                        var colNum = cell.Address.ColumnNumber;
                        // Ensure the header list is large enough (1-based, so index = colNum - 1)
                        while (headers.Count < colNum)
                            headers.Add(string.Empty);

                        var headerText = cell.GetString().Trim();
                        headers[colNum - 1] = headerText;

                        if (cell.IsEmpty()) continue;

                        // Header-row tokens are labelled by the column name itself so the
                        // chip reads exactly as the column it represents; fall back to the
                        // spreadsheet column letter when the header cell is blank.
                        var label = string.IsNullOrWhiteSpace(headerText)
                            ? $"Col {ColumnLetter(colNum)} · header row"
                            : headerText;

                        tokens.Add(new SourceToken(
                            Id:    $"cell:r{rowNum}c{colNum}",
                            Label: label,
                            Value: headerText,
                            Group: "header"
                        ));
                    }
                    continue;
                }

                // Data rows (row 2+)
                foreach (var cell in row.Cells())
                {
                    if (cell.IsEmpty()) continue;

                    var colNum   = cell.Address.ColumnNumber;
                    var value    = cell.GetString().Trim();

                    // Prefer the column header name ("Unit Price · row 9"); fall back to the
                    // spreadsheet column letter ("Col C · row 9") when no header is available.
                    var colLabel = colNum <= headers.Count && !string.IsNullOrWhiteSpace(headers[colNum - 1])
                        ? headers[colNum - 1]
                        : $"Col {ColumnLetter(colNum)}";

                    tokens.Add(new SourceToken(
                        Id:    $"cell:r{rowNum}c{colNum}",
                        Label: $"{colLabel} · row {rowNum}",
                        Value: value,
                        Group: "line"
                    ));
                }
            }

            return tokens;
        }
        catch
        {
            // Malformed or unreadable XLSX — return empty; never throw from the tokenizer.
            return Array.Empty<SourceToken>();
        }
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SourceToken> TokenizeCsv(byte[] bytes)
    {
        // Detect delimiter using the same peek logic as CsvOrderParser.
        var delimiter = DetectCsvDelimiter(bytes);

        var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            Delimiter         = delimiter,
            HeaderValidated   = null!,
            MissingFieldFound = null!,
            HasHeaderRecord   = false, // read raw rows so we control row/col addressing
        };

        var tokens  = new List<SourceToken>();
        var headers = new List<string>(); // column header labels (from row 1)
        int rowIndex = 0;

        using var ms     = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv    = new CsvReader(reader, config);

        while (csv.Read())
        {
            rowIndex++;
            var fieldCount = csv.Parser.Count;

            for (int col = 0; col < fieldCount; col++)
            {
                var value  = csv.GetField(col) ?? string.Empty;
                var colNum = col + 1; // 1-based column number for labelling

                // Prefer the column header name ("Unit Price · row 9"); fall back to the
                // column letter ("Col C · row 9") when the header cell is blank/missing.
                var colLabel = col < headers.Count && !string.IsNullOrWhiteSpace(headers[col])
                    ? headers[col]
                    : $"Col {ColumnLetter(colNum)}";

                if (rowIndex == 1)
                {
                    // First row is the header; capture it for labelling subsequent rows.
                    headers.Add(value);

                    // Header-row tokens are labelled by the column name itself; fall back to
                    // the column letter when the header cell is blank.
                    var label = string.IsNullOrWhiteSpace(value)
                        ? $"Col {ColumnLetter(colNum)} · header row"
                        : value;
                    tokens.Add(new SourceToken(
                        Id:    $"cell:r{rowIndex}c{colNum}",
                        Label: label,
                        Value: value,
                        Group: "header"
                    ));
                }
                else
                {
                    tokens.Add(new SourceToken(
                        Id:    $"cell:r{rowIndex}c{colNum}",
                        Label: $"{colLabel} · row {rowIndex}",
                        Value: value,
                        Group: "line"
                    ));
                }
            }
        }

        return tokens;
    }

    private static string DetectCsvDelimiter(byte[] bytes)
    {
        using var ms     = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var firstLine = reader.ReadLine() ?? string.Empty;
        return firstLine.Contains(';') && !firstLine.Contains(',') ? ";" : ",";
    }

    /// <summary>
    /// Converts a 1-based column number to its spreadsheet column letter
    /// (1→A, 2→B, 26→Z, 27→AA, …) for use in human-readable fallback labels.
    /// </summary>
    private static string ColumnLetter(int colNum)
    {
        if (colNum < 1) return colNum.ToString();
        var sb = new StringBuilder();
        while (colNum > 0)
        {
            var rem = (colNum - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            colNum = (colNum - 1) / 26;
        }
        return sb.ToString();
    }

    // ── XML ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SourceToken> TokenizeXml(byte[] bytes)
    {
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(bytes);
            doc = XDocument.Load(ms, System.Xml.Linq.LoadOptions.None);
        }
        catch (XmlException)
        {
            // Malformed XML — return empty, caller sees no tokens.
            return Array.Empty<SourceToken>();
        }

        var tokens = new List<SourceToken>();
        if (doc.Root is null) return tokens;

        // Walk the document depth-first; emit leaf text + all attributes.
        // - id   keeps the raw XPath (stable address) — UNCHANGED.
        // - label uses a short readable path of element local-names (prefix preserved),
        //   e.g. "cbc:ID" or "OrderRequestHeader@orderID".
        WalkXmlElement(
            doc.Root,
            path:          "/" + XmlLocalName(doc.Root),
            selfDisplay:   XmlReadableName(doc.Root),
            parentDisplay: string.Empty,
            tokens:        tokens);
        return tokens;
    }

    /// <param name="selfDisplay">
    ///   This element's readable display name including any positional marker assigned by its
    ///   parent (e.g. <c>"Line[2]"</c>). Passed down to become each child's parentDisplay.
    /// </param>
    /// <param name="parentDisplay">
    ///   The immediate parent element's display name (or empty at the root). Used to give leaf
    ///   labels a short parent context, e.g. <c>"Line[2] › ItemCode"</c>.
    /// </param>
    private static void WalkXmlElement(
        XElement element, string path, string selfDisplay, string parentDisplay, List<SourceToken> tokens)
    {
        // Readable name for this element: prefix-qualified local name, e.g. "cbc:ID".
        var selfLabel = XmlReadableName(element);

        // Emit attributes on this element. Label = "{ownerLocalName}@{attrName}",
        // e.g. "OrderRequestHeader@orderID". The id keeps the raw XPath.
        foreach (var attr in element.Attributes())
        {
            var attrPath  = $"{path}/@{attr.Name.LocalName}";
            var attrLabel = $"{element.Name.LocalName}@{attr.Name.LocalName}";
            tokens.Add(new SourceToken(
                Id:    attrPath,
                Label: attrLabel,
                Value: attr.Value,
                Group: null
            ));
        }

        var children = element.Elements().ToList();

        if (children.Count == 0)
        {
            // Leaf element — emit its text value.
            // Label shows the parent context when present ("Line[2] › cbc:ID") so common
            // names like "ID" remain distinguishable; otherwise just the element name.
            var text = element.Value ?? string.Empty;
            var leafLabel = string.IsNullOrEmpty(parentDisplay)
                ? selfLabel
                : $"{parentDisplay} › {selfLabel}";
            tokens.Add(new SourceToken(
                Id:    path,
                Label: leafLabel,
                Value: text,
                Group: null
            ));
        }
        else
        {
            // Group children by local name so we can add positional predicates for repeating siblings.
            var nameGroups = children
                .GroupBy(c => c.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.Count());

            // Track per-name occurrence index for stable predicate assignment.
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var child in children)
            {
                var localName  = child.Name.LocalName;
                var isRepeated = nameGroups[localName] > 1;

                if (!nameIndex.TryGetValue(localName, out var idx))
                    idx = 0;
                nameIndex[localName] = idx + 1;

                var childPath = isRepeated
                    ? $"{path}/{localName}[{idx + 1}]"
                    : $"{path}/{localName}";

                // The child's own display carries a positional marker when it repeats so
                // its descendant leaf tokens stay distinguishable (e.g. "Line[2] › ItemCode").
                var childDisplay = isRepeated
                    ? $"{XmlReadableName(child)}[{idx + 1}]"
                    : XmlReadableName(child);

                WalkXmlElement(child, childPath, childDisplay, selfDisplay, tokens);
            }
        }
    }

    private static string XmlLocalName(XElement element) => element.Name.LocalName;

    /// <summary>
    /// Returns a short, human-readable element name for a label: the namespace prefix
    /// plus the local name when the document declares a prefix (e.g. <c>cbc:ID</c>),
    /// otherwise just the local name (e.g. <c>PoNumber</c>). The raw XPath stays in the
    /// token id; this is purely for display.
    /// </summary>
    private static string XmlReadableName(XElement element)
    {
        var local = element.Name.LocalName;
        var ns    = element.Name.Namespace;
        if (ns == XNamespace.None) return local;

        var prefix = element.GetPrefixOfNamespace(ns);
        return string.IsNullOrEmpty(prefix) ? local : $"{prefix}:{local}";
    }

    // ── EDIFACT ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenises an EDIFACT ORDERS message (UN/EDIFACT D96A, D01B-tolerant).
    /// Token id scheme: <c>"seg:{TAG}[{n}].el{element}"</c> for simple (single-component)
    /// elements, and <c>"seg:{TAG}[{n}].el{element}.c{component}"</c> for multi-component
    /// composites, where:
    /// <list type="bullet">
    ///   <item><c>TAG</c> — segment tag, e.g. <c>BGM</c>, <c>LIN</c>.</item>
    ///   <item><c>n</c> — 1-based occurrence count of that tag in the whole message
    ///         (stable for a deterministic file layout).</item>
    ///   <item><c>element</c> — 1-based element index within the segment.</item>
    ///   <item><c>component</c> — 1-based component index within a composite element
    ///         (omitted when the element has only one non-empty component).</item>
    /// </list>
    /// Group is "header" for segments that appear before the first LIN, "line" for
    /// segments from the first LIN onward.
    ///
    /// Uses the same delimiter-resolution logic as <see cref="ProcuLink.Transform.Parsing.EdifactOrderParser"/>
    /// (UNA prefix → custom delimiters; defaults: component ':', element '+', segment '\'').
    /// Returns an empty list on malformed input rather than throwing.
    /// </summary>
    private static IReadOnlyList<SourceToken> TokenizeEdifact(byte[] bytes)
    {
        try
        {
            string raw;
            using (var ms = new MemoryStream(bytes))
            using (var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                raw = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<SourceToken>();

            var (compSep, elemSep, relSep, segSep) = EdifactDelimiters(raw);

            // Strip the 9-char UNA prefix when present (same logic as EdifactOrderParser.StripUnaPrefix).
            string body;
            var rawTrimmed = raw.AsSpan().TrimStart();
            if (rawTrimmed.Length >= 9 && rawTrimmed.StartsWith("UNA"))
            {
                var leadingWhitespace = raw.Length - rawTrimmed.Length;
                body = raw.Substring(leadingWhitespace + 9);
            }
            else
            {
                body = raw;
            }

            var tokens      = new List<SourceToken>();
            var tagCounts   = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenLinYet  = false;

            foreach (var rawSeg in SplitEdifactSegments(body, segSep, relSep))
            {
                var parts = SplitEdifactElements(rawSeg, elemSep, relSep);
                if (parts.Count == 0) continue;

                var tag = parts[0].ToUpperInvariant();
                if (tag.Length == 0) continue;

                if (!tagCounts.TryGetValue(tag, out var count)) count = 0;
                count++;
                tagCounts[tag] = count;

                if (tag == "LIN") seenLinYet = true;
                var group = seenLinYet ? "line" : "header";

                // Build the segment label prefix, e.g. "BGM[1]"
                var segPrefix = $"seg:{tag}[{count}]";

                // Occurrence suffix only when the same tag repeats (keeps "BGM document no"
                // clean while still disambiguating "LIN item code #2").
                var occ = count > 1 ? $" #{count}" : string.Empty;

                for (int elIdx = 1; elIdx < parts.Count; elIdx++)
                {
                    var elementRaw = parts[elIdx];
                    var components = SplitEdifactComponents(elementRaw, compSep, relSep);

                    if (components.Count == 1)
                    {
                        // Simple element (no composite) — id ends at the element index.
                        var val = Unescape(components[0], relSep);
                        if (string.IsNullOrEmpty(val)) continue;

                        var meaning = EdifactMeaning(tag, elIdx, component: 0);
                        var label   = meaning is null
                            ? $"{tag} element {elIdx}{occ}"
                            : $"{tag} {meaning}{occ}";

                        tokens.Add(new SourceToken(
                            Id:    $"{segPrefix}.el{elIdx}",
                            Label: label,
                            Value: val,
                            Group: group
                        ));
                    }
                    else
                    {
                        // Composite element — emit each non-empty component.
                        for (int cIdx = 0; cIdx < components.Count; cIdx++)
                        {
                            var val = Unescape(components[cIdx], relSep);
                            if (string.IsNullOrEmpty(val)) continue;

                            var meaning = EdifactMeaning(tag, elIdx, component: cIdx + 1);
                            var label   = meaning is null
                                ? $"{tag} element {elIdx} component {cIdx + 1}{occ}"
                                : $"{tag} {meaning}{occ}";

                            tokens.Add(new SourceToken(
                                Id:    $"{segPrefix}.el{elIdx}.c{cIdx + 1}",
                                Label: label,
                                Value: val,
                                Group: group
                            ));
                        }
                    }
                }
            }

            return tokens;
        }
        catch
        {
            // Malformed EDIFACT — return empty, never throw.
            return Array.Empty<SourceToken>();
        }
    }

    /// <summary>
    /// Resolves EDIFACT delimiters: reads the optional UNA service-string advice (9 chars)
    /// or falls back to the standard defaults (component ':', element '+', release '?',
    /// segment '\'). Returns (componentSep, elementSep, releaseSep, segmentSep).
    /// </summary>
    private static (char comp, char elem, char rel, char seg) EdifactDelimiters(string raw)
    {
        var span = raw.AsSpan().TrimStart();
        if (span.Length >= 9 && span.StartsWith("UNA"))
        {
            // UNA layout: positions 3-8 → comp, elem, decimal, release, reserved, segment
            return (comp: span[3], elem: span[4], rel: span[6], seg: span[8]);
        }
        return (comp: ':', elem: '+', rel: '?', seg: '\'');
    }

    /// <summary>
    /// Splits an EDIFACT body into raw segment strings on the segment terminator,
    /// respecting release-escaped terminators but PRESERVING the release sequence
    /// in the output so that element and component splitting can later consume it.
    /// An escaped segment terminator is emitted literally (as release + char) rather
    /// than splitting the segment. Whitespace between segments is ignored.
    /// </summary>
    private static IEnumerable<string> SplitEdifactSegments(string body, char segTerm, char release)
    {
        var sb      = new StringBuilder();
        var escaped = false;

        foreach (var c in body)
        {
            if (escaped)
            {
                // Preserve the escape sequence for the downstream element/component splitters.
                sb.Append(release);
                sb.Append(c);
                escaped = false;
                continue;
            }
            if (c == release) { escaped = true; continue; }
            if (c == segTerm)
            {
                var s = sb.ToString().Trim('\r', '\n', ' ', '\t');
                sb.Clear();
                if (s.Length > 0) yield return s;
                continue;
            }
            if (sb.Length == 0 && (c == '\r' || c == '\n' || c == ' ' || c == '\t')) continue;
            sb.Append(c);
        }

        var tail = sb.ToString().Trim('\r', '\n', ' ', '\t');
        if (tail.Length > 0) yield return tail;
    }

    /// <summary>Splits an EDIFACT segment string into tag + element parts on the element
    /// separator, preserving release sequences (not unescaping).</summary>
    private static List<string> SplitEdifactElements(string segment, char elemSep, char release)
    {
        var parts   = new List<string>();
        var sb      = new StringBuilder();
        var escaped = false;

        foreach (var c in segment)
        {
            if (escaped) { sb.Append(release); sb.Append(c); escaped = false; continue; }
            if (c == release) { escaped = true; continue; }
            if (c == elemSep) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Splits an EDIFACT composite element into components on the component
    /// separator, preserving release sequences (unescaping happens in Unescape).</summary>
    private static List<string> SplitEdifactComponents(string element, char compSep, char release)
    {
        var parts   = new List<string>();
        var sb      = new StringBuilder();
        var escaped = false;

        foreach (var c in element)
        {
            if (escaped) { sb.Append(release); sb.Append(c); escaped = false; continue; }
            if (c == release) { escaped = true; continue; }
            if (c == compSep) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Strips EDIFACT release escaping from a leaf value (the release char is
    /// dropped and the following char is taken literally).</summary>
    private static string Unescape(string value, char release)
    {
        if (!value.Contains(release)) return value;

        var sb      = new StringBuilder(value.Length);
        var escaped = false;
        foreach (var c in value)
        {
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == release) { escaped = true; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── X12 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenises an ANSI ASC X12 850 interchange.
    /// Token id scheme: <c>"seg:{TAG}[{n}].el{element}"</c> where:
    /// <list type="bullet">
    ///   <item><c>TAG</c> — segment tag, e.g. <c>BEG</c>, <c>PO1</c>.</item>
    ///   <item><c>n</c> — 1-based occurrence count of that tag in the interchange.</item>
    ///   <item><c>element</c> — 1-based element index (after the tag).</item>
    /// </list>
    /// X12 has no release/escape character; simple split on the segment terminator is correct.
    /// Delimiters are discovered from the ISA segment (element separator at position 3,
    /// segment terminator at position 105 of the fixed-width ISA). Falls back to the
    /// canonical defaults (element '*', segment '~') when the ISA is absent or non-conformant.
    ///
    /// Group is "header" for segments before the first PO1, "line" from PO1 onward.
    /// Returns an empty list on malformed input rather than throwing.
    /// </summary>
    private static IReadOnlyList<SourceToken> TokenizeX12(byte[] bytes)
    {
        try
        {
            string raw;
            using (var ms = new MemoryStream(bytes))
            using (var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                raw = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<SourceToken>();

            var (elemSep, segTerm) = X12Delimiters(raw);

            var tokens     = new List<SourceToken>();
            var tagCounts  = new Dictionary<string, int>(StringComparer.Ordinal);
            var seenPo1    = false;

            foreach (var rawSeg in raw.Split(segTerm))
            {
                var trimmed = rawSeg.Trim('\r', '\n', ' ', '\t');
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split(elemSep);
                if (parts.Length == 0) continue;

                var tag = parts[0].Trim().ToUpperInvariant();
                if (tag.Length == 0) continue;

                if (!tagCounts.TryGetValue(tag, out var count)) count = 0;
                count++;
                tagCounts[tag] = count;

                if (tag == "PO1") seenPo1 = true;
                var group = seenPo1 ? "line" : "header";

                var segPrefix = $"seg:{tag}[{count}]";

                // Occurrence suffix only when the same tag repeats.
                var occ = count > 1 ? $" #{count}" : string.Empty;

                for (int elIdx = 1; elIdx < parts.Length; elIdx++)
                {
                    var val = parts[elIdx].Trim();
                    if (string.IsNullOrEmpty(val)) continue;

                    var meaning = X12Meaning(tag, elIdx);
                    var label   = meaning is null
                        ? $"{tag} element {elIdx}{occ}"
                        : $"{tag} {meaning}{occ}";

                    tokens.Add(new SourceToken(
                        Id:    $"{segPrefix}.el{elIdx}",
                        Label: label,
                        Value: val,
                        Group: group
                    ));
                }
            }

            return tokens;
        }
        catch
        {
            // Malformed X12 — return empty, never throw.
            return Array.Empty<SourceToken>();
        }
    }

    /// <summary>
    /// Resolves X12 delimiters from the ISA segment.
    /// Element separator  = ISA[3]       (char immediately after "ISA")
    /// Segment terminator = ISA[105]     (char immediately after ISA16)
    /// Falls back to default '*' / '~' when ISA is absent or too short.
    /// </summary>
    private static (char elem, char seg) X12Delimiters(string raw)
    {
        var span = raw.AsSpan().TrimStart();
        if (span.Length > 105
            && span.StartsWith("ISA")
            && !char.IsLetterOrDigit(span[3])
            && !char.IsLetterOrDigit(span[105]))
        {
            return (elem: span[3], seg: span[105]);
        }

        // Partial ISA: at least pick up the element separator from position 3.
        if (span.Length > 3 && span.StartsWith("ISA") && !char.IsLetterOrDigit(span[3]))
            return (elem: span[3], seg: '~');

        return (elem: '*', seg: '~');
    }

    // ── EDIFACT / X12 element-meaning lookups (display labels only) ─────────────

    /// <summary>
    /// Returns a short human-readable meaning for a known EDIFACT ORDERS element/component
    /// (e.g. <c>BGM</c> el2 → <c>"document no"</c>, <c>QTY</c> el1 c2 → <c>"quantity"</c>),
    /// or <c>null</c> when the position is not in the table (caller falls back to
    /// <c>"{TAG} element {n}"</c>). Keyed on UN/EDIFACT D96A ORDERS segment layouts.
    /// <paramref name="component"/> is 0 for a simple element, or the 1-based component
    /// index inside a composite.
    /// </summary>
    private static string? EdifactMeaning(string tag, int element, int component) => (tag, element, component) switch
    {
        // BGM — Beginning of message
        ("BGM", 1, 0) => "document name code",
        ("BGM", 1, 1) => "document name code",
        ("BGM", 2, 0) => "document no",
        ("BGM", 3, 0) => "message function",
        // DTM — Date/time/period (composite C507)
        ("DTM", 1, 1) => "date qualifier",
        ("DTM", 1, 2) => "date",
        ("DTM", 1, 3) => "date format",
        // NAD — Name and address
        ("NAD", 1, 0) => "party qualifier",
        ("NAD", 2, 1) => "party id",
        ("NAD", 4, 0) => "party name",
        ("NAD", 4, 1) => "party name",
        // CUX — Currencies (composite C504)
        ("CUX", 1, 1) => "currency usage",
        ("CUX", 1, 2) => "currency",
        ("CUX", 1, 3) => "currency qualifier",
        // LIN — Line item
        ("LIN", 1, 0) => "line number",
        ("LIN", 3, 1) => "item code",
        ("LIN", 3, 2) => "item code type",
        // IMD — Item description (composite C273 — text in components 3+)
        ("IMD", 3, 3) => "item description",
        ("IMD", 3, 4) => "item description",
        // QTY — Quantity (composite C186)
        ("QTY", 1, 1) => "quantity qualifier",
        ("QTY", 1, 2) => "quantity",
        ("QTY", 1, 3) => "unit of measure",
        // PRI — Price details (composite C509)
        ("PRI", 1, 1) => "price qualifier",
        ("PRI", 1, 2) => "unit price",
        // MOA — Monetary amount (composite C516)
        ("MOA", 1, 1) => "amount qualifier",
        ("MOA", 1, 2) => "amount",
        // RFF — Reference (composite C506)
        ("RFF", 1, 1) => "reference qualifier",
        ("RFF", 1, 2) => "reference number",
        _ => null,
    };

    /// <summary>
    /// Returns a short human-readable meaning for a known ANSI X12 850 element
    /// (e.g. <c>BEG</c> el3 → <c>"PO number"</c>, <c>PO1</c> el2 → <c>"quantity"</c>),
    /// or <c>null</c> when the position is not in the table (caller falls back to
    /// <c>"{TAG} element {n}"</c>).
    /// </summary>
    private static string? X12Meaning(string tag, int element) => (tag, element) switch
    {
        // BEG — Beginning segment for purchase order
        ("BEG", 1) => "purpose code",
        ("BEG", 2) => "PO type",
        ("BEG", 3) => "PO number",
        ("BEG", 5) => "PO date",
        // PO1 — Baseline item data
        ("PO1", 1) => "line number",
        ("PO1", 2) => "quantity",
        ("PO1", 3) => "unit of measure",
        ("PO1", 4) => "unit price",
        ("PO1", 6) => "product id qualifier",
        ("PO1", 7) => "item code",
        // REF — Reference identification
        ("REF", 1) => "reference qualifier",
        ("REF", 2) => "reference number",
        // DTM — Date/time reference
        ("DTM", 1) => "date qualifier",
        ("DTM", 2) => "date",
        // N1 — Name
        ("N1", 1) => "entity qualifier",
        ("N1", 2) => "party name",
        ("N1", 4) => "party id",
        // PID — Product/item description
        ("PID", 5) => "item description",
        // CUR — Currency
        ("CUR", 1) => "currency entity",
        ("CUR", 2) => "currency",
        _ => null,
    };
}
