using System.Globalization;
using System.Text;
using System.Xml;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Transform.Detection;

/// <summary>
/// Best-effort extractor for the SOURCE field names of an uploaded purchase-order file,
/// across every format ProcuLink ingests. See <see cref="ISourceColumnExtractor"/>.
///
/// Lives in <c>ProcuLink.Transform</c> because that is where the file-format dependencies
/// already live (CsvHelper, ClosedXML, System.Xml). It is intentionally self-contained:
/// format resolution uses the supplied hint, then the file extension, then a tiny content
/// sniff — it does NOT depend on the Infrastructure <c>FormatDetectorService</c>, so the
/// Transform layer stays free of an Infrastructure reference.
///
/// Safety contract: never throws. Any failure during extraction collapses to an empty
/// column list with the best-known format label.
/// </summary>
public sealed class SourceColumnExtractor : ISourceColumnExtractor
{
    /// <summary>
    /// Leading window read for content-sniffing when neither a format hint nor a useful
    /// extension is available. 1 KiB covers an XML declaration + root + namespaces, an
    /// EDIFACT/X12 envelope, and the first CSV line.
    /// </summary>
    private const int SniffBytes = 1024;

    public async Task<SourceColumnSet> ExtractAsync(
        Stream content,
        string? fileName,
        string? formatHint,
        CancellationToken ct)
    {
        // Buffer once so we can sniff + rewind + parse regardless of stream seekability.
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception)
        {
            return SourceColumnSet.Empty(NormalizeHint(formatHint) ?? "unknown");
        }

        if (bytes.Length == 0)
            return SourceColumnSet.Empty(NormalizeHint(formatHint) ?? "unknown");

        var format = ResolveFormat(formatHint, fileName, bytes);

        try
        {
            var columns = format switch
            {
                "csv"     => ExtractCsv(bytes),
                "xlsx"    => ExtractXlsx(bytes),
                "cxml"    => ExtractXml(bytes),
                "ubl"     => ExtractXml(bytes),
                "xml"     => ExtractXml(bytes),
                "edifact" => ExtractEdi(bytes),
                "x12"     => ExtractEdi(bytes),
                "pdf"     => Array.Empty<string>(),   // best-effort: not feasible without a layout model
                _         => Array.Empty<string>(),
            };

            return new SourceColumnSet(format, Dedupe(columns));
        }
        catch (Exception)
        {
            // Sniffer contract: never throw — degrade to an empty list with the format we settled on.
            return SourceColumnSet.Empty(format);
        }
    }

    // ── Format resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolution order: explicit hint → file extension → content sniff → "unknown".
    /// </summary>
    private static string ResolveFormat(string? formatHint, string? fileName, byte[] bytes)
    {
        var hint = NormalizeHint(formatHint);
        if (hint is not null) return hint;

        var fromExt = FromExtension(fileName);
        if (fromExt is not null) return fromExt;

        return Sniff(bytes);
    }

    /// <summary>
    /// Maps a caller-supplied format string (e.g. a <see cref="DetectedFormat.Format"/>) onto
    /// the labels this extractor understands. Returns null for null/blank/unrecognised hints
    /// so resolution can fall through to the extension + sniff passes.
    /// </summary>
    private static string? NormalizeHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        return hint.Trim().ToLowerInvariant() switch
        {
            "csv"                       => "csv",
            "xlsx" or "xls"             => "xlsx",
            "cxml"                      => "cxml",
            "ubl"                       => "ubl",
            "xml"                       => "xml",
            "edifact" or "edi"          => "edifact",
            "x12"                       => "x12",
            "pdf"                       => "pdf",
            _                           => null,
        };
    }

    private static string? FromExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return null;
        var ext = fileName.Substring(dot + 1).ToLowerInvariant();
        return ext switch
        {
            "csv"           => "csv",
            "xlsx" or "xls" => "xlsx",
            "cxml"          => "cxml",
            "ubl"           => "ubl",
            "xml"           => "xml",   // generic XML — extractor handles UBL/cXML/other uniformly
            "edi"           => "edifact",
            "x12"           => "x12",
            "pdf"           => "pdf",
            _               => null,
        };
    }

    /// <summary>Minimal magic-byte / envelope sniff for when extension + hint are absent.</summary>
    private static string Sniff(byte[] bytes)
    {
        var read = Math.Min(bytes.Length, SniffBytes);

        // PDF — "%PDF-"
        if (read >= 5 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D)
            return "pdf";

        // XLSX / ZIP container — "PK\x03\x04"
        if (read >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            return "xlsx";

        var head = Encoding.UTF8.GetString(bytes, 0, read);
        var trimmed = head.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (trimmed.StartsWith("<?xml", StringComparison.Ordinal) || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            if (head.Contains("<cXML", StringComparison.OrdinalIgnoreCase) || head.Contains("cxml.org/cXML", StringComparison.Ordinal))
                return "cxml";
            if (head.Contains("urn:oasis:names:specification:ubl:schema:xsd:Order-2", StringComparison.Ordinal))
                return "ubl";
            return "xml";
        }

        var head200 = head.Length <= 200 ? head : head.Substring(0, 200);
        if (trimmed.StartsWith("UNA", StringComparison.Ordinal) || head200.Contains("UNB+", StringComparison.Ordinal))
            return "edifact";

        if (trimmed.StartsWith("ISA", StringComparison.Ordinal))
            return "x12";

        // Last resort: if line 1 has a common CSV separator, call it CSV.
        var firstLine = head.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? string.Empty;
        if (firstLine.Contains(',') || firstLine.Contains(';') || firstLine.Contains('\t'))
            return "csv";

        return "unknown";
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the header-row cells. Delimiter detection mirrors <c>CsvOrderParser</c>:
    /// semicolon when the header contains ';' and no ',', otherwise comma.
    /// </summary>
    private static IReadOnlyList<string> ExtractCsv(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);

        // Peek line 1 for the delimiter without consuming the parse stream.
        string firstLine;
        using (var peek = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            firstLine = peek.ReadLine() ?? string.Empty;
        }
        var delimiter = firstLine.Contains(';') && !firstLine.Contains(',') ? ";"
                      : firstLine.Contains('\t') && !firstLine.Contains(',') ? "\t"
                      : ",";

        ms.Position = 0;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter         = delimiter,
            HasHeaderRecord   = true,
            MissingFieldFound = null!,
            BadDataFound      = null!,
            HeaderValidated   = null!,
        };

        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv    = new CsvReader(reader, config);

        if (!csv.Read()) return Array.Empty<string>();
        csv.ReadHeader();

        return (csv.HeaderRecord ?? Array.Empty<string>())
            .Select(h => h?.Trim().Trim('"') ?? string.Empty)
            .Where(h => h.Length > 0)
            .ToList();
    }

    // ── XLSX ────────────────────────────────────────────────────────────────

    /// <summary>Returns the header-row cells from the first worksheet (row 1).</summary>
    private static IReadOnlyList<string> ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(ms);

        var worksheet = workbook.Worksheets.FirstOrDefault();
        var headerRow = worksheet?.RangeUsed()?.RowsUsed().FirstOrDefault();
        if (headerRow is null) return Array.Empty<string>();

        return headerRow.Cells()
            .Select(c => c.GetString().Trim())
            .Where(name => name.Length > 0)
            .ToList();
    }

    // ── XML (UBL / cXML / generic) ────────────────────────────────────────────

    /// <summary>
    /// Walks the XML with a streaming <see cref="XmlReader"/> and collects:
    /// <list type="bullet">
    ///   <item>leaf-element local-names (elements that carry text and have no element children), and</item>
    ///   <item>attributes rendered as <c>Element@attr</c> when the attribute carries a value
    ///         (so e.g. <c>OrderRequestHeader@orderID</c> and <c>Quantity@unitCode</c> surface).</item>
    /// </list>
    /// The column identity is the element/attribute <b>LocalName</b> only — the namespace
    /// prefix is deliberately dropped. A prefix is a property of the document, not the field:
    /// two semantically identical UBL Orders (one binding <c>xmlns:cbc</c>, one declaring the
    /// same namespace as the default) would otherwise emit different names (<c>cbc:ID</c> vs
    /// <c>ID</c>). Because these strings are the persisted mapping keys, prefix-style differences
    /// would silently break mapping reuse. Emitting the bare LocalName keeps the identity stable.
    /// </summary>
    private static IReadOnlyList<string> ExtractXml(byte[] bytes)
    {
        var fields = new List<string>();

        using var ms = new MemoryStream(bytes);
        var settings = new XmlReaderSettings
        {
            IgnoreComments              = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing               = DtdProcessing.Prohibit,
            CloseInput                  = false,
        };

        using var reader = XmlReader.Create(ms, settings);

        // Track whether the element we last opened has produced child elements; if it ends
        // without children but had text, it is a leaf worth surfacing.
        var elementHasChildStack = new Stack<bool>();
        string? pendingLeafName = null;
        var pendingHasText = false;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    // Opening a new element means the parent (if any) has at least one child element.
                    if (elementHasChildStack.Count > 0)
                    {
                        elementHasChildStack.Pop();
                        elementHasChildStack.Push(true);
                    }

                    var name = LocalIdentity(reader);

                    // Attributes — record Element@attr for each value-bearing attribute (skip xmlns).
                    if (reader.HasAttributes)
                    {
                        for (var i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (IsNamespaceDeclaration(reader)) continue;
                            if (string.IsNullOrWhiteSpace(reader.Value)) continue;
                            fields.Add($"{name}@{LocalIdentity(reader)}");
                        }
                        reader.MoveToElement();
                    }

                    if (reader.IsEmptyElement)
                    {
                        // Self-closing element with no text — its attributes (if any) were captured above.
                        break;
                    }

                    // Descend: this element is, so far, child-less and text-less.
                    elementHasChildStack.Push(false);
                    pendingLeafName = name;
                    pendingHasText = false;
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (!string.IsNullOrWhiteSpace(reader.Value))
                        pendingHasText = true;
                    break;

                case XmlNodeType.EndElement:
                    var hadChild = elementHasChildStack.Count > 0 && elementHasChildStack.Pop();
                    // A leaf = no child elements. Surface it when it carried text (data field).
                    if (!hadChild && pendingHasText && pendingLeafName is not null)
                        fields.Add(pendingLeafName);

                    pendingLeafName = null;
                    pendingHasText = false;
                    break;
            }
        }

        return fields;
    }

    /// <summary>
    /// Column identity for an XML element or attribute: the <b>LocalName</b> only, with the
    /// namespace prefix dropped so prefix style cannot change the persisted mapping key.
    ///
    /// Known limitation: two leaves that share a LocalName under different namespaces (e.g.
    /// a buyer-side and supplier-side <c>ID</c>) will collide onto one column identity. A full
    /// namespace-URI-keyed identity would disambiguate them; that is acceptable future work.
    /// </summary>
    private static string LocalIdentity(XmlReader reader) => reader.LocalName;

    private static bool IsNamespaceDeclaration(XmlReader reader) =>
        string.Equals(reader.Name, "xmlns", StringComparison.Ordinal)
        || reader.Name.StartsWith("xmlns:", StringComparison.Ordinal);

    // ── EDIFACT / X12 ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the distinct segment tags present, in first-seen order. Works for both EDIFACT
    /// (':'/'+' delimiters, "'" terminator) and X12 ('*'/'>' delimiters, '~' terminator) because
    /// it only needs the leading segment tag of each segment, which is the alphabetic run before
    /// the first non-alphanumeric delimiter. Robust to CR/LF noise and missing trailing terminators.
    /// </summary>
    private static IReadOnlyList<string> ExtractEdi(byte[] bytes)
    {
        var text = DecodeText(bytes);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // Segment terminators we recognise: EDIFACT "'", X12 "~", plus newlines as a fallback.
        var rawSegments = text.Split(new[] { '\'', '~', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        var tags = new List<string>();
        foreach (var raw in rawSegments)
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;

            // The tag is the leading run of letters/digits (UN-tags are 3 letters; X12 like "PO1"
            // include a digit). Stop at the first delimiter character.
            var sb = new StringBuilder();
            foreach (var c in seg)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
                else break;
            }

            var tag = sb.ToString();
            // EDIFACT/X12 segment tags are 2-3 chars. Guard against free text being misread as a tag.
            if (tag.Length is >= 2 and <= 3)
                tags.Add(tag);
        }

        return tags;
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Shared ─────────────────────────────────────────────────────────────────

    /// <summary>De-duplicates while preserving first-seen order (case-sensitive — field names matter).</summary>
    private static IReadOnlyList<string> Dedupe(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var trimmed = v.Trim();
            if (seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }
}
