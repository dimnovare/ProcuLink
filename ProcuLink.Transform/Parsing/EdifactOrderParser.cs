using System.Globalization;
using System.Text;

namespace ProcuLink.Transform.Parsing;

// ─────────────────────────────────────────────────────────────────────────────
// Library decision (2026-05-28):
//
// Investigated `indice.Edi` (indice-co/edi.net, MIT) and `EdiFabric` (commercial).
// Decision: roll our own minimal segment-level parser.
//
// Reasoning:
//   1. EDIFACT ORDERS D96A has a stable, well-known segment grammar. The 95%
//      coverage set (UNA, UNB, UNH, BGM, DTM, NAD, CUX, LIN, PIA, IMD, QTY,
//      PRI, UNS, UNT, UNZ) maps directly onto `ParsedOrder` / `ParsedOrderLine`.
//   2. `indice.Edi` is attribute/POCO-driven and requires a full D96A ORDERS
//      message-dictionary class hierarchy to deserialize cleanly. That is
//      ~600 LOC of generated POCOs for one message version. We'd write more
//      glue than parser.
//   3. `EdiFabric` is commercial (~€1,500/yr) — explicitly flagged in
//      `docs/format-channel-roadmap.md` as a future option once ORDRSP /
//      DESADV / INVOIC ship. Premature for D-Day-One ORDERS-only support.
//   4. Segment-level parsing handles delimiter customisation (UNA segment),
//      escape sequences, and D01B vs D96A variance with a few lines of code.
//      Both versions share the segments we care about; the differences are in
//      composite element ordering which our positional access tolerates.
//
// Re-evaluate when: ORDRSP / DESADV outbound is on the roadmap, or when a
// customer hits a segment we don't handle (PCD, ALC, TAX line breakdown).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parses EDIFACT ORDERS messages (UN/EDIFACT D96A primary, D01B-tolerant) into
/// a <see cref="ParsedOrder"/>.
///
/// Coverage:
///   UNA   — service string advice (custom delimiters; optional)
///   UNB   — interchange header (sender/recipient/datetime; for context only)
///   UNH   — message header (validates ORDERS message type)
///   BGM   — beginning of message (PO number)
///   DTM   — date/time/period (137 = document/order date used as OrderDate)
///   NAD   — name and address (BY = buyer used for BuyerName)
///   CUX   — currency
///   LIN   — line item (buyer item code)
///   IMD   — item description (free-text)
///   QTY   — quantity (21 = ordered qty)
///   PRI   — price (AAA = net unit price)
///   UNS   — section control (skipped)
///   UNT   — message trailer (skipped)
///   UNZ   — interchange trailer (skipped)
///
/// Selected segments parsed elsewhere (PIA, ALC, RFF, MOA, TAX, etc.) are
/// intentionally ignored — they are not required by the canonical PO model.
/// </summary>
public sealed class EdifactOrderParser : IPurchaseOrderParser
{
    // EDIFACT default delimiters (per ISO 9735). Overridden by UNA when present.
    private const char DefaultComponent = ':';
    private const char DefaultElement   = '+';
    private const char DefaultDecimal   = '.';
    private const char DefaultRelease   = '?';
    private const char DefaultSegment   = '\'';

    /// <summary>
    /// Returns true for <c>.edi</c>. <c>.txt</c> and content-type sniffing
    /// (<c>application/edifact</c>, <c>application/edi-x12</c>) require the
    /// caller / factory to do content-based dispatch — see
    /// <see cref="LooksLikeEdifact"/>. The current <see cref="OrderParserFactory"/>
    /// is extension-only, so we only claim <c>.edi</c> here.
    /// </summary>
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".edi", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Public content-sniffing helper for the eventual factory upgrade that
    /// dispatches by both extension and content-type. Returns true when the
    /// first non-whitespace bytes look like an EDIFACT envelope (UNA or UNB).
    /// </summary>
    public static bool LooksLikeEdifact(string head)
    {
        if (string.IsNullOrWhiteSpace(head)) return false;
        var trimmed = head.TrimStart();
        return trimmed.StartsWith("UNA", StringComparison.Ordinal)
            || trimmed.StartsWith("UNB", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true for the two content-types we accept. <c>application/edi-x12</c>
    /// is a common but technically incorrect synonym we tolerate.
    /// </summary>
    public static bool IsEdifactContentType(string? contentType) =>
        string.Equals(contentType, "application/edifact",  StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/edi-x12", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new EdifactParseException("EDIFACT message is empty.");

        var delimiters = ResolveDelimiters(raw);
        var body = StripUnaPrefix(raw);

        List<Segment> segments;
        try
        {
            segments = Tokenize(body, delimiters);
        }
        catch (Exception ex) when (ex is not EdifactParseException)
        {
            throw new EdifactParseException($"Failed to tokenize EDIFACT segments: {ex.Message}", ex);
        }

        if (segments.Count == 0)
            throw new EdifactParseException("EDIFACT message contains no segments.");

        // ── Validate that this is an ORDERS message ────────────────────────────
        var unh = segments.FirstOrDefault(s => s.Tag == "UNH")
            ?? throw new EdifactParseException("Required UNH segment is missing.");

        // UNH+ref+ORDERS:D:96A:UN
        //   element 1 = message reference number (e.g. "1")
        //   element 2 = S009 composite (message identifier): ORDERS:D:96A:UN
        // The message type is component 0 of element 2.
        var messageType = unh.Component(2, 0);
        if (!string.Equals(messageType, "ORDERS", StringComparison.OrdinalIgnoreCase))
            throw new EdifactParseException(
                $"Expected ORDERS message type in UNH; found '{messageType ?? "(missing)"}'.");

        var bgm = segments.FirstOrDefault(s => s.Tag == "BGM")
            ?? throw new EdifactParseException("Required BGM segment is missing.");

        // BGM+220+PO12345+9
        //   element 1 = document/message name (C002 composite or simple 1001) — e.g. "220" = order
        //   element 2 = document/message number (1004) — the PO number
        //   element 3 = message function (1225) — e.g. "9" = original
        var poNumber = NullIfEmpty(bgm.Element(2));

        // ── Header-level scans ────────────────────────────────────────────────
        DateTime? orderDate = null;
        string? buyerName   = null;
        string? currency    = null;

        // We walk header segments until the first LIN (line section starts there).
        var headerSegments = segments.TakeWhile(s => s.Tag != "LIN");

        foreach (var seg in headerSegments)
        {
            switch (seg.Tag)
            {
                case "DTM":
                    // DTM+137:20240115:102  → 137=document date, 102=CCYYMMDD format
                    var dtmQualifier = seg.Component(1, 0);
                    if (string.Equals(dtmQualifier, "137", StringComparison.Ordinal))
                    {
                        orderDate ??= ParseEdifactDate(seg.Component(1, 1), seg.Component(1, 2));
                    }
                    break;

                case "NAD":
                    // NAD+BY+5412345678901::9++Acme Buyer Ltd+...
                    // qualifier at element 1; party-name composite at element 4 (D96A)
                    var nadQualifier = seg.Element(1);
                    if (string.Equals(nadQualifier, "BY", StringComparison.Ordinal) && buyerName is null)
                    {
                        buyerName = ExtractPartyName(seg);
                    }
                    break;

                case "CUX":
                    // CUX+2:EUR:9  → 2 = order currency
                    var cuxQualifier = seg.Component(1, 0);
                    if (string.Equals(cuxQualifier, "2", StringComparison.Ordinal) && currency is null)
                    {
                        currency = NullIfEmpty(seg.Component(1, 1))?.ToUpperInvariant();
                    }
                    break;
            }
        }

        // ── Line items ─────────────────────────────────────────────────────────
        var lines = ParseLines(segments);

        return new ParsedOrder(
            PoNumber:  poNumber,
            OrderDate: orderDate,
            BuyerName: buyerName,
            Currency:  currency,
            Lines:     lines);
    }

    // ── Line parsing ─────────────────────────────────────────────────────────

    private static IReadOnlyList<ParsedOrderLine> ParseLines(List<Segment> segments)
    {
        var lines  = new List<ParsedOrderLine>();
        int auto   = 1;

        // LIN starts a line group. Subsequent segments (IMD/QTY/PRI/...) belong to
        // the current line until the next LIN or UNS / UNT.
        Segment? currentLin = null;
        var      lineDescr  = (string?)null;
        var      lineQty    = 0m;
        var      lineUnit   = (string?)null;
        var      linePrice  = (decimal?)null;
        var      lineNumber = 0;
        var      lineCode   = string.Empty;

        void Flush()
        {
            if (currentLin is null) return;
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
                case "LIN":
                    Flush();
                    currentLin = seg;
                    lineDescr  = null;
                    lineQty    = 0m;
                    lineUnit   = null;
                    linePrice  = null;

                    // LIN+1++ITEM-CODE:IN
                    var lnNumRaw = seg.Element(1);
                    lineNumber = int.TryParse(lnNumRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln)
                        ? ln
                        : auto;
                    // Item code lives in element 3 component 0 (3 = item number identification, 7140)
                    lineCode = NullIfEmpty(seg.Component(3, 0)) ?? string.Empty;
                    auto++;
                    break;

                case "IMD" when currentLin is not null:
                    // IMD+F+ANM+:::Widget Type A
                    // Description text typically occupies components 3+ of element 3.
                    var parts = new List<string?>
                    {
                        seg.Component(3, 3),
                        seg.Component(3, 4),
                    };
                    var descr = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                    lineDescr = NullIfEmpty(descr) ?? lineDescr;
                    break;

                case "QTY" when currentLin is not null:
                    // QTY+21:100:PCE → 21 = ordered quantity
                    var qtyQualifier = seg.Component(1, 0);
                    if (string.Equals(qtyQualifier, "21", StringComparison.Ordinal)
                        || string.Equals(qtyQualifier, "1",  StringComparison.Ordinal))
                    {
                        lineQty  = ParseDecimal(seg.Component(1, 1)) ?? 0m;
                        lineUnit = NullIfEmpty(seg.Component(1, 2)) ?? lineUnit;
                    }
                    break;

                case "PRI" when currentLin is not null:
                    // PRI+AAA:25.00 → AAA = net unit price
                    var priQualifier = seg.Component(1, 0);
                    if (string.Equals(priQualifier, "AAA", StringComparison.Ordinal)
                        || string.Equals(priQualifier, "AAB", StringComparison.Ordinal))
                    {
                        linePrice = ParseDecimal(seg.Component(1, 1));
                    }
                    break;

                case "UNS":
                case "UNT":
                    Flush();
                    currentLin = null;
                    break;
            }
        }

        // Flush final line if file ends without UNS/UNT (defensive)
        Flush();
        return lines;
    }

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private static List<Segment> Tokenize(string body, Delimiters d)
    {
        var segments = new List<Segment>();
        var current  = new StringBuilder();
        var released = false;

        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (released)
            {
                // Escaped character — emit literally.
                current.Append(c);
                released = false;
                continue;
            }

            if (c == d.Release)
            {
                released = true;
                continue;
            }

            if (c == d.Segment)
            {
                var raw = current.ToString().Trim('\r', '\n', ' ', '\t');
                current.Clear();
                if (raw.Length == 0) continue;

                segments.Add(ParseSegment(raw, d));
                continue;
            }

            // Skip control whitespace between segments
            if (current.Length == 0 && (c == '\r' || c == '\n' || c == ' ' || c == '\t'))
                continue;

            current.Append(c);
        }

        // Capture trailing segment without terminator (lenient)
        var tail = current.ToString().Trim('\r', '\n', ' ', '\t');
        if (tail.Length > 0)
            segments.Add(ParseSegment(tail, d));

        return segments;
    }

    private static Segment ParseSegment(string raw, Delimiters d)
    {
        var elementParts = SplitRespectingRelease(raw, d.Element, d.Release);
        if (elementParts.Count == 0)
            throw new EdifactParseException("Encountered empty segment.");

        var tag = elementParts[0].ToUpperInvariant();
        var elements = new List<List<string>>(elementParts.Count - 1);

        for (int i = 1; i < elementParts.Count; i++)
        {
            var comps = SplitRespectingRelease(elementParts[i], d.Component, d.Release);
            elements.Add(comps);
        }

        return new Segment(tag, elements);
    }

    private static List<string> SplitRespectingRelease(string input, char delimiter, char release)
    {
        var result = new List<string>();
        var sb     = new StringBuilder();
        var esc    = false;

        foreach (var c in input)
        {
            if (esc)
            {
                sb.Append(c);
                esc = false;
                continue;
            }

            if (c == release)
            {
                esc = true;
                continue;
            }

            if (c == delimiter)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }

    // ── UNA handling ─────────────────────────────────────────────────────────

    private static Delimiters ResolveDelimiters(string raw)
    {
        // UNA is exactly 9 chars: "UNA" + 6 delimiter characters.
        // Layout: UNA<comp><elem><dec><release><reserved><segment>
        var trimmedStart = raw.AsSpan().TrimStart();
        if (trimmedStart.Length >= 9 && trimmedStart.StartsWith("UNA"))
        {
            return new Delimiters(
                Component: trimmedStart[3],
                Element:   trimmedStart[4],
                Decimal:   trimmedStart[5],
                Release:   trimmedStart[6],
                Segment:   trimmedStart[8]);
        }

        return new Delimiters(
            Component: DefaultComponent,
            Element:   DefaultElement,
            Decimal:   DefaultDecimal,
            Release:   DefaultRelease,
            Segment:   DefaultSegment);
    }

    private static string StripUnaPrefix(string raw)
    {
        var trimmed = raw.AsSpan().TrimStart();
        if (trimmed.Length >= 9 && trimmed.StartsWith("UNA"))
        {
            // Cut the first 9 characters of the trimmed view from the original.
            var leading = raw.Length - trimmed.Length;
            return raw.Substring(leading + 9);
        }
        return raw;
    }

    // ── Field extraction helpers ─────────────────────────────────────────────

    private static string? ExtractPartyName(Segment nad)
    {
        // D96A NAD: element 4 is "PARTY NAME" composite (C080) with up to five
        // 35-char name lines. Real-world messages typically place the company
        // name in component 0; line 2+ is rare for buyer/supplier.
        var nameParts = new List<string?>
        {
            nad.Component(4, 0),
            nad.Component(4, 1),
            nad.Component(4, 2),
            nad.Component(4, 3),
            nad.Component(4, 4),
        };

        var joined = string.Join(" ", nameParts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim()));

        return NullIfEmpty(joined);
    }

    private static DateTime? ParseEdifactDate(string? value, string? formatCode)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();

        // Common DTM format codes:
        //   102 = CCYYMMDD
        //   101 = YYMMDD
        //   203 = CCYYMMDDHHMM
        //   204 = CCYYMMDDHHMMSS
        var formats = (formatCode ?? "102") switch
        {
            "101" => new[] { "yyMMdd" },
            "102" => new[] { "yyyyMMdd" },
            "203" => new[] { "yyyyMMddHHmm" },
            "204" => new[] { "yyyyMMddHHmmss" },
            _     => new[] { "yyyyMMdd", "yyMMdd", "yyyyMMddHHmm", "yyyy-MM-dd" },
        };

        if (DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
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

    // ── Internal types ───────────────────────────────────────────────────────

    private readonly record struct Delimiters(
        char Component,
        char Element,
        char Decimal,
        char Release,
        char Segment);

    /// <summary>
    /// One EDIFACT segment, e.g. <c>NAD+BY+...+Acme Buyer:Ltd+...</c>.
    /// Elements and components are stored positionally; absent positions
    /// return null from <see cref="Element"/> / <see cref="Component"/>.
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
