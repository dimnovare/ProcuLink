using System.Globalization;
using System.Text;

namespace ProcuLink.Transform.Parsing;

// ─────────────────────────────────────────────────────────────────────────────
// Library decision (2026-05-29):
//
// Hand-rolled, consistent with EdifactOrderParser. No commercial EDI library
// (EdiFabric ~€1,500/yr is explicitly off the table per founder policy; see
// docs/.../memory). X12 850 shares EDIFACT's flat-segment shape: split on the
// segment terminator, split each segment on the element separator, split each
// element on the component separator. The 95% coverage set for a Purchase Order
// (ISA, GS, ST, BEG, REF, DTM, CUR, N1, PO1, PID, CTT, SE, GE, IEA) maps
// directly onto ParsedOrder / ParsedOrderLine.
//
// Unlike EDIFACT, X12 has no release/escape character and the delimiters are
// not advertised in a service-string segment — they are discovered positionally
// from the fixed-width ISA segment:
//   element separator   = the character immediately after "ISA" (index 3)
//   component separator  = ISA16                                   (index 104)
//   segment terminator   = the character immediately after ISA16   (index 105)
//
// Re-evaluate when 855 (PO Ack) / 856 (ASN) / 810 (Invoice) outbound is on the
// roadmap, or when a customer hits a segment we don't handle.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parses ANSI ASC X12 850 Purchase Order interchanges (versions 004010 /
/// 005010) into a <see cref="ParsedOrder"/>.
///
/// Coverage:
///   ISA — interchange control header (delimiter discovery; context only)
///   GS  — functional group header (skipped)
///   ST  — transaction set header (validates 850)
///   BEG — beginning segment for PO (BEG03 = PO number, BEG05 = order date)
///   CUR — currency (CUR02 = ISO currency code)
///   DTM — date/time reference (optional; skipped — order date comes from BEG05)
///   N1  — name (N101 = BY → buyer name)
///   PO1 — baseline item data (qty, UOM, unit price, item-id qualifier pairs)
///   PID — product description (PID05 → line description for preceding PO1)
///   CTT — transaction totals (skipped)
///   SE / GE / IEA — trailers (skipped)
///
/// Item identification qualifier pairs in PO1 (positions 6+) follow the
/// <c>qualifier, value</c> convention. Buyer-side codes (<c>BP</c> buyer part,
/// <c>IN</c> buyer item number) populate <see cref="ParsedOrderLine.BuyerItemCode"/>;
/// when absent we fall back to vendor codes (<c>VP</c>/<c>VN</c>) so a line
/// always carries an identifier. Supplier codes are NOT trusted as the resolved
/// SupplierItemCode — that is resolved downstream from the mappings table.
///
/// Validation: throws <see cref="X12ParseException"/> when the interchange is
/// not a parseable 850 (missing ISA, no ST*850, missing BEG). Optional and
/// unknown segments are skipped — never throws on malformed optional segments.
/// </summary>
public sealed class X12OrderParser : IPurchaseOrderParser
{
    // X12 default delimiters (per the canonical 004010 example). Overridden by
    // the positional ISA discovery when an ISA segment is present.
    private const char DefaultElement   = '*';
    private const char DefaultComponent = '>';
    private const char DefaultSegment   = '~';

    /// <summary>
    /// Claims <c>.x12</c> only. <c>.edi</c> is shared with EDIFACT and <c>.txt</c>
    /// is ambiguous, so content-based dispatch for those extensions is handled by
    /// <see cref="OrderParserFactory"/> via <see cref="IsX12OrderDocument(string)"/>.
    /// </summary>
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".x12", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Content-sniffing helper for the factory. Returns true when the head looks
    /// like an X12 interchange that carries an 850 transaction set: it starts
    /// with <c>ISA</c> and contains an <c>ST</c> segment whose first element is
    /// <c>850</c>. The element separator is taken from the character after
    /// <c>ISA</c> when available, falling back to <c>*</c>.
    /// </summary>
    public static bool IsX12OrderDocument(string head)
    {
        if (string.IsNullOrWhiteSpace(head)) return false;
        var t = head.TrimStart();
        if (!t.StartsWith("ISA", StringComparison.Ordinal)) return false;

        if (t.Length > 3)
        {
            var sep = t[3];
            if (t.Contains($"ST{sep}850", StringComparison.Ordinal)) return true;
        }
        return t.Contains("ST*850", StringComparison.Ordinal);
    }

    /// <summary>
    /// Stream overload used by the factory: reads a window, restores the stream
    /// position, and applies <see cref="IsX12OrderDocument(string)"/>.
    /// </summary>
    public static bool IsX12OrderDocument(Stream peek)
    {
        if (!peek.CanSeek) return false;
        var start = peek.Position;
        try
        {
            var buf  = new byte[1024];
            var read = peek.Read(buf, 0, buf.Length);
            var head = Encoding.UTF8.GetString(buf, 0, read);
            return IsX12OrderDocument(head);
        }
        finally
        {
            peek.Position = start;
        }
    }

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new X12ParseException("X12 interchange is empty.");

        // Note: use string ops (not ReadOnlySpan) — ref structs can't live across
        // the await boundary above in C# 12 (CS9202).
        if (!raw.TrimStart().StartsWith("ISA", StringComparison.Ordinal))
            throw new X12ParseException("X12 interchange must begin with an ISA segment.");

        var delimiters = ResolveDelimiters(raw);

        List<Segment> segments;
        try
        {
            segments = Tokenize(raw, delimiters);
        }
        catch (Exception ex) when (ex is not X12ParseException)
        {
            throw new X12ParseException($"Failed to tokenize X12 segments: {ex.Message}", ex);
        }

        if (segments.Count == 0)
            throw new X12ParseException("X12 interchange contains no segments.");

        // ── Validate this is an 850 transaction set ────────────────────────────
        // ST*850*0001  → element 1 = transaction set identifier code
        var st = segments.FirstOrDefault(s => s.Tag == "ST");
        if (st is null)
            throw new X12ParseException("Required ST (transaction set header) segment is missing.");

        var transactionSet = st.Element(1);
        if (!string.Equals(transactionSet, "850", StringComparison.Ordinal))
            throw new X12ParseException(
                $"Expected transaction set 850 in ST; found '{transactionSet ?? "(missing)"}'.");

        // ── BEG — beginning segment for purchase order (required) ──────────────
        // BEG*00*NE*PO-12345**20260529
        //   01 = transaction set purpose code   02 = purchase order type code
        //   03 = purchase order number          04 = release number
        //   05 = date (CCYYMMDD)
        var beg = segments.FirstOrDefault(s => s.Tag == "BEG");
        if (beg is null)
            throw new X12ParseException("Required BEG (beginning segment for purchase order) is missing.");

        var poNumber  = NullIfEmpty(beg.Element(3));
        var orderDate = ParseX12Date(beg.Element(5));

        // ── Header-level scans (everything before the first PO1) ───────────────
        string? buyerName = null;
        string? currency  = null;

        foreach (var seg in segments.TakeWhile(s => s.Tag != "PO1"))
        {
            switch (seg.Tag)
            {
                case "CUR":
                    // CUR*BY*USD  → 01 = entity qualifier, 02 = ISO currency code
                    currency ??= NullIfEmpty(seg.Element(2))?.ToUpperInvariant();
                    break;

                case "N1":
                    // N1*BY*Buyer Name  → 01 = entity identifier code, 02 = name
                    if (buyerName is null && string.Equals(seg.Element(1), "BY", StringComparison.Ordinal))
                        buyerName = NullIfEmpty(seg.Element(2));
                    break;
            }
        }

        var lines = ParseLines(segments);

        return new ParsedOrder(
            PoNumber:  poNumber,
            OrderDate: orderDate,
            BuyerName: buyerName,
            Currency:  currency,
            Lines:     lines);
    }

    // ── Line parsing ───────────────────────────────────────────────────────────

    private static IReadOnlyList<ParsedOrderLine> ParseLines(List<Segment> segments)
    {
        var lines = new List<ParsedOrderLine>();
        int auto  = 1;

        // PO1 starts a line group. A following PID (and any unknown segment) belongs
        // to the current line until the next PO1 or the CTT/SE trailer.
        Segment? currentPo1 = null;
        var lineDescr  = (string?)null;
        var lineQty    = 0m;
        var lineUnit   = (string?)null;
        var linePrice  = (decimal?)null;
        var lineNumber = 0;
        var lineCode   = string.Empty;

        void Flush()
        {
            if (currentPo1 is null) return;
            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: lineCode,
                Description:   NullIfEmpty(lineDescr),
                Quantity:      lineQty,
                Unit:          NullIfEmpty(lineUnit),
                UnitPrice:     linePrice));
        }

        foreach (var seg in segments)
        {
            switch (seg.Tag)
            {
                case "PO1":
                    Flush();
                    currentPo1 = seg;
                    lineDescr  = null;

                    // PO1*1*12*EA*4.50*PE*BP*ACME-WIDGET-A*VP*SUP-001
                    //   01 = assigned identifier (line number)
                    //   02 = quantity ordered     03 = unit of measure
                    //   04 = unit price            05 = basis of unit price code
                    //   06+ = (product/service id qualifier, value) pairs
                    var lnRaw = seg.Element(1);
                    lineNumber = int.TryParse(lnRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln)
                        ? ln
                        : auto;
                    lineQty   = ParseDecimal(seg.Element(2)) ?? 0m;
                    lineUnit  = NullIfEmpty(seg.Element(3));
                    linePrice = ParseDecimal(seg.Element(4));
                    lineCode  = ExtractItemCode(seg) ?? string.Empty;
                    auto++;
                    break;

                case "PID" when currentPo1 is not null:
                    // PID*F****Widget A 10mm
                    //   01 = item description type (F = free-form)
                    //   05 = description
                    lineDescr = NullIfEmpty(seg.Element(5)) ?? lineDescr;
                    break;

                case "CTT":
                case "SE":
                    Flush();
                    currentPo1 = null;
                    break;
            }
        }

        // Flush the final line if the interchange ends without CTT/SE (defensive).
        Flush();
        return lines;
    }

    /// <summary>
    /// Walks the PO1 product/service identification pairs (elements 6+) and
    /// returns the buyer-side item code (<c>BP</c>/<c>IN</c>), falling back to a
    /// vendor code (<c>VP</c>/<c>VN</c>) and then any first value so a line always
    /// has an identifier.
    /// </summary>
    private static string? ExtractItemCode(Segment po1)
    {
        string? buyer    = null;
        string? vendor   = null;
        string? fallback = null;

        // Elements are 1-based: 6,8,10,... are qualifiers; 7,9,11,... are values.
        for (int qualIdx = 6; qualIdx < po1.ElementCount; qualIdx += 2)
        {
            var qualifier = po1.Element(qualIdx);
            var value     = NullIfEmpty(po1.Element(qualIdx + 1));
            if (value is null) continue;

            fallback ??= value;

            switch (qualifier)
            {
                case "BP":
                case "IN":
                    buyer ??= value;
                    break;
                case "VP":
                case "VN":
                    vendor ??= value;
                    break;
            }
        }

        return buyer ?? vendor ?? fallback;
    }

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private static List<Segment> Tokenize(string raw, Delimiters d)
    {
        var segments = new List<Segment>();

        // X12 has no release/escape character; a simple split is correct. We trim
        // control whitespace (CR/LF/tab/space) that real files insert between
        // segments for readability.
        foreach (var rawSegment in raw.Split(d.Segment))
        {
            var trimmed = rawSegment.Trim('\r', '\n', ' ', '\t');
            if (trimmed.Length == 0) continue;
            segments.Add(ParseSegment(trimmed, d));
        }

        return segments;
    }

    private static Segment ParseSegment(string raw, Delimiters d)
    {
        var elementParts = raw.Split(d.Element);
        var tag = elementParts[0].Trim().ToUpperInvariant();

        var elements = new List<List<string>>(elementParts.Length - 1);
        for (int i = 1; i < elementParts.Length; i++)
            elements.Add(elementParts[i].Split(d.Component).ToList());

        return new Segment(tag, elements);
    }

    // ── ISA delimiter discovery ────────────────────────────────────────────────

    private static Delimiters ResolveDelimiters(string raw)
    {
        // ISA is fixed-width: "ISA" + 16 elements separated by the element
        // separator, total 105 characters (indices 0..104), with the segment
        // terminator at index 105.
        //   index 3   = element separator (char after "ISA")
        //   index 104 = ISA16 = component (sub-element) separator
        //   index 105 = segment terminator
        var span = raw.AsSpan();
        var leading = raw.Length - span.TrimStart().Length;
        var t = raw.AsSpan(leading);

        // The positional layout only holds for a conformant fixed-width ISA. Guard
        // against truncated / hand-built envelopes: delimiters are always
        // punctuation, so if either positional slot is alphanumeric we treat the
        // ISA as non-conformant and fall back to the canonical defaults.
        if (t.Length > 105 && t.StartsWith("ISA")
            && !char.IsLetterOrDigit(t[3])
            && !char.IsLetterOrDigit(t[104])
            && !char.IsLetterOrDigit(t[105]))
        {
            return new Delimiters(
                Element:   t[3],
                Component: t[104],
                Segment:   t[105]);
        }

        // Element separator at index 3 is reliable even when the ISA is short.
        var element = t.Length > 3 && !char.IsLetterOrDigit(t[3]) ? t[3] : DefaultElement;

        return new Delimiters(
            Element:   element,
            Component: DefaultComponent,
            Segment:   DefaultSegment);
    }

    // ── Field helpers ────────────────────────────────────────────────────────

    private static DateTime? ParseX12Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();

        // 004010 uses CCYYMMDD (8); legacy 003xxx used YYMMDD (6).
        string[] formats = { "yyyyMMdd", "yyMMdd" };
        if (DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
            ? dt
            : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Contains(',') && !normalized.Contains('.'))
            normalized = normalized.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Internal types ─────────────────────────────────────────────────────────

    private readonly record struct Delimiters(char Element, char Component, char Segment);

    /// <summary>
    /// One X12 segment, e.g. <c>PO1*1*12*EA*4.50*PE*BP*ACME-WIDGET-A</c>. Elements
    /// and components are stored positionally; absent positions return null from
    /// <see cref="Element(int)"/> / <see cref="Component(int, int)"/>.
    /// </summary>
    private sealed class Segment
    {
        public string Tag { get; }
        public IReadOnlyList<IReadOnlyList<string>> Elements { get; }

        public Segment(string tag, IReadOnlyList<IReadOnlyList<string>> elements)
        {
            Tag = tag;
            Elements = elements;
        }

        /// <summary>Number of elements after the tag (1-based access tops out here).</summary>
        public int ElementCount => Elements.Count;

        public string? Element(int index)
        {
            if (index < 1 || index > Elements.Count) return null;
            var comps = Elements[index - 1];
            return comps.Count == 0 ? null : comps[0];
        }

        public string? Component(int elementIndex, int componentIndex)
        {
            if (elementIndex < 1 || elementIndex > Elements.Count) return null;
            var comps = Elements[elementIndex - 1];
            if (componentIndex < 0 || componentIndex >= comps.Count) return null;
            return comps[componentIndex];
        }
    }
}
