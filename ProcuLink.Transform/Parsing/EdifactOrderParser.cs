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
///   QTY   — quantity (DE 6063: 21 = ordered, 1 = discrete; any other qualifier is
///           read but the line is flagged for review — never a silent 0)
///   PRI   — price (DE 5125: AAA/AAB/CAL = calculation price; any other qualifier,
///           e.g. the information price AAE, is read but the line is flagged)
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
    /// Returns true for <c>.edi</c> AND <c>.txt</c>. EDIFACT interchanges are
    /// routinely delivered with a <c>.txt</c> extension, so an extension-only
    /// gate (e.g. <see cref="OrderParserFactory.GetParser(string)"/>) must let a
    /// <c>.txt</c> through to this parser instead of rejecting it as an
    /// unsupported format before the content-sniffing stream overload runs.
    ///
    /// Claiming <c>.txt</c> here means a NON-EDIFACT <c>.txt</c> (plain text) can
    /// also reach <see cref="ParseAsync"/> as a fallback when no other parser
    /// claims it — that is SAFE: <see cref="ParseAsync"/> fails LOUDLY (a typed
    /// <see cref="EdifactParseException"/> for a missing UNH/ORDERS envelope, and
    /// the no-segments guard), so a free-text <c>.txt</c> never produces a silent
    /// empty/0-line "success". Content-type sniffing
    /// (<c>application/edifact</c>, <c>application/edi-x12</c>) is handled by the
    /// factory via <see cref="LooksLikeEdifact"/>; X12 <c>.txt</c> is sniffed and
    /// routed to the X12 parser before this extension fallback.
    /// </summary>
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".edi", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileExtension, ".txt", StringComparison.OrdinalIgnoreCase);

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
        // Thread the UNA-declared decimal mark through so numeric values are read in the
        // sender's locale. Without it, an EU file that declares ',' as the decimal mark
        // (UNA:+,? ') had its quantities/prices parsed under a hard-coded '.' decimal, so
        // "1.234,56" failed to parse (→ null) and an EU-grouped "1.000" was silently
        // under-read as 1.0 instead of 1000 (~1000× wrong). See ParseDecimal.
        var lines = ParseLines(segments, delimiters.Decimal);

        return new ParsedOrder(
            PoNumber:  poNumber,
            OrderDate: orderDate,
            BuyerName: buyerName,
            Currency:  currency,
            Lines:     lines);
    }

    // ── Line parsing ─────────────────────────────────────────────────────────

    private static IReadOnlyList<ParsedOrderLine> ParseLines(List<Segment> segments, char decimalMark)
    {
        var lines  = new List<ParsedOrderLine>();
        int auto   = 1;

        // LIN starts a line group. Subsequent segments (IMD/QTY/PRI/...) belong to
        // the current line until the next LIN or UNS / UNT.
        Segment? currentLin    = null;
        var      lineDescr     = (string?)null;
        var      lineQty       = 0m;
        var      lineUnit      = (string?)null;
        var      linePrice     = (decimal?)null;
        var      lineNumber    = 0;
        var      lineCode      = string.Empty;
        var      qtyAmbiguous   = false;
        var      priceAmbiguous = false;
        // Qualifier provenance for the current line. A quantity or price is only
        // authoritative when it came from an accepted qualifier (see
        // IsAcceptedQuantityQualifier / IsAcceptedPriceQualifier); anything else is a
        // last-resort fallback that has to be flagged, and the *QualifierUsed fields
        // carry the code so the review reason can name it.
        var      qtyRead            = false;
        var      qtyFromAccepted    = false;
        var      qtyQualifierUsed   = (string?)null;
        var      priceRead          = false;
        var      priceFromAccepted  = false;
        var      priceQualifierUsed = (string?)null;

        void Flush()
        {
            if (currentLin is null) return;
            // A quantity that never came from an ordered-quantity qualifier is not the
            // quantity ordered: it may be a pack size, a free-goods count, or — when the
            // line carries no QTY at all — nothing but the 0 reset above. Either way the
            // line must surface for review rather than be delivered as though it had been
            // read. A price is only questionable once one was actually read under a
            // non-calculation qualifier; a line with no PRI keeps an honest null.
            var qtyUnconfirmed   = !qtyFromAccepted;
            var priceUnconfirmed = priceRead && !priceFromAccepted;
            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: lineCode,
                Description:   NullIfEmpty(lineDescr),
                Quantity:      lineQty,
                Unit:          NullIfEmpty(lineUnit),
                UnitPrice:     linePrice,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the parser
                // could not read unambiguously in the declared locale — or could read but cannot
                // treat as authoritative — flags the line so it surfaces for human review instead
                // of being delivered ~10×/100×/1000× wrong, or as a plausible 0. Mirrors
                // CsvOrderParser's NeedsReview/ReviewReason contract.
                NeedsReview:   qtyAmbiguous || priceAmbiguous || qtyUnconfirmed || priceUnconfirmed,
                ReviewReason:  BuildLineReviewReason(
                                   qtyAmbiguous, priceAmbiguous,
                                   qtyUnconfirmed, qtyRead, qtyQualifierUsed,
                                   priceUnconfirmed, priceQualifierUsed)));
        }

        foreach (var seg in segments)
        {
            switch (seg.Tag)
            {
                case "LIN":
                    Flush();
                    currentLin     = seg;
                    lineDescr      = null;
                    lineQty        = 0m;
                    lineUnit       = null;
                    linePrice      = null;
                    qtyAmbiguous   = false;
                    priceAmbiguous = false;
                    qtyRead            = false;
                    qtyFromAccepted    = false;
                    qtyQualifierUsed   = null;
                    priceRead          = false;
                    priceFromAccepted  = false;
                    priceQualifierUsed = null;

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
                {
                    // QTY+21:100:PCE → DE 6063 qualifier 21 = ordered quantity.
                    var qtyQualifier = seg.Component(1, 0);
                    var qtyAccepted  = IsAcceptedQuantityQualifier(qtyQualifier);
                    // An accepted qualifier is authoritative wherever it appears and
                    // overwrites a fallback already taken; an unrecognised one is read only
                    // while nothing has supplied a quantity yet. So segment order never
                    // decides the answer, and a second unrecognised QTY cannot displace the
                    // first. Reading the unrecognised value (instead of dropping it, which
                    // left a silent 0) gives the reviewer the candidate number — the flag
                    // set in Flush is what stops it being delivered.
                    if (qtyAccepted || !qtyRead)
                    {
                        var (qtyVal, qtyAmb) = ParseDecimal(seg.Component(1, 1), decimalMark);
                        lineQty      = qtyVal ?? 0m;
                        qtyAmbiguous = qtyAmb;
                        lineUnit = NullIfEmpty(seg.Component(1, 2)) ?? lineUnit;
                        qtyRead          = true;
                        qtyFromAccepted  = qtyAccepted;
                        qtyQualifierUsed = qtyAccepted ? null : NullIfEmpty(qtyQualifier);
                    }
                    break;
                }

                case "PRI" when currentLin is not null:
                {
                    // PRI+AAA:25.00 → DE 5125 qualifier AAA = net calculation price.
                    var priQualifier = seg.Component(1, 0);
                    var priAccepted  = IsAcceptedPriceQualifier(priQualifier);
                    if (priAccepted || !priceRead)
                    {
                        var (priceVal, priceAmb) = ParseDecimal(seg.Component(1, 1), decimalMark);
                        linePrice      = priceVal;
                        priceAmbiguous = priceAmb;
                        priceRead          = true;
                        priceFromAccepted  = priAccepted;
                        priceQualifierUsed = priAccepted ? null : NullIfEmpty(priQualifier);
                    }
                    break;
                }

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
                // Escaped character. Tokenizing only needs to NOT treat an escaped
                // segment terminator as a real terminator — it must PRESERVE the
                // release sequence (both the release char and the escaped char) so
                // that release-unescaping happens exactly once, later, at the
                // component split in SplitRespectingRelease. Consuming the release
                // here (as a naive "emit literally") would unescape a second time.
                current.Append(d.Release);
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

        // A dangling release char at the very end is malformed, but preserve it so
        // tokenizing is lossless (the downstream split decides how to treat it).
        if (released) current.Append(d.Release);

        // Capture trailing segment without terminator (lenient)
        var tail = current.ToString().Trim('\r', '\n', ' ', '\t');
        if (tail.Length > 0)
            segments.Add(ParseSegment(tail, d));

        return segments;
    }

    private static Segment ParseSegment(string raw, Delimiters d)
    {
        // Split into elements while PRESERVING release sequences: an escaped element
        // delimiter must not split, but its release prefix is kept intact so the
        // component split below is the single point that unescapes.
        var elementParts = SplitPreservingRelease(raw, d.Element, d.Release);
        if (elementParts.Count == 0)
            throw new EdifactParseException("Encountered empty segment.");

        var tag = elementParts[0].ToUpperInvariant();
        var elements = new List<List<string>>(elementParts.Count - 1);

        for (int i = 1; i < elementParts.Count; i++)
        {
            // Component split is the LEAF split and the ONLY place release-escaping is
            // removed. Every element value (even a simple single-component element)
            // passes through here, so each leaf value is unescaped exactly once.
            var comps = SplitRespectingRelease(elementParts[i], d.Component, d.Release);
            elements.Add(comps);
        }

        return new Segment(tag, elements);
    }

    /// <summary>
    /// Splits <paramref name="input"/> on every UNescaped <paramref name="delimiter"/>,
    /// but leaves release sequences intact in the output (the release char and the
    /// char it escapes are both preserved). Used for the segment and element splits,
    /// which must respect — but not consume — escaping so that the final component
    /// split (<see cref="SplitRespectingRelease"/>) performs the single unescape.
    /// </summary>
    private static List<string> SplitPreservingRelease(string input, char delimiter, char release)
    {
        var result = new List<string>();
        var sb     = new StringBuilder();
        var esc    = false;

        foreach (var c in input)
        {
            if (esc)
            {
                // Preserve the whole release sequence for the leaf split to unescape.
                sb.Append(release);
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

        // Lossless: keep a dangling release char rather than silently dropping it.
        if (esc) sb.Append(release);

        result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Splits <paramref name="input"/> on every UNescaped <paramref name="delimiter"/>
    /// and removes release escaping (the release char is dropped, the next char is
    /// taken literally). This is the LEAF split — release-unescaping happens here and
    /// nowhere else, so a value is never unescaped twice.
    /// </summary>
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

    /// <summary>
    /// Parse an EDIFACT numeric value honouring the UNA-declared <paramref name="decimalMark"/>
    /// ('.' by ISO 9735 default, but ',' in many EU files). The OTHER of '.'/',' is treated as a
    /// thousands/grouping separator. Returns <c>(value, ambiguous)</c>; <c>ambiguous</c> is true when
    /// the token could not be read unambiguously and the parser refuses to guess (the caller flags the
    /// line for review rather than emitting a silently-wrong number). A blank token is NOT ambiguous
    /// (a legitimately absent optional value → null).
    ///
    /// Mirrors <see cref="CsvOrderParser"/>'s locale-safe "last separator is the decimal; refuse-and-
    /// flag genuine ambiguity" reader, with the declared decimal mark as the locale signal:
    ///   • a comma decimal mark ⇒ European, so a lone "1.000" (3 trailing digits) is grouping → 1000,
    ///     "1.234,56" → 1234.56, "73,22" → 73.22;
    ///   • a dot decimal mark (ISO default) keeps "12.50"/"125.5"/"3.50" as decimals.
    /// This replaces the previous hard-coded '.'-only reader that read "1.234,56" as null and an
    /// EU-grouped "1.000" as 1.0 (~1000× under-read), both silently.
    /// </summary>
    private static (decimal? Value, bool Ambiguous) ParseDecimal(string? value, char decimalMark)
        // A comma decimal mark is the locale signal that this is a European number (where '.' groups
        // thousands). Anything else (the ISO default '.') reads as US/invariant where '.' is decimal.
        // Delegates to the shared NumberParsing reader so EDIFACT, CSV, X12, UBL and cXML all read the
        // same EU/US number identically.
        => NumberParsing.TryParseFlexibleDecimal(value, european: decimalMark == ',');

    private static string? BuildAmbiguityReason(bool qtyAmbiguous, bool priceAmbiguous) =>
        NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous);

    // ── Segment qualifier policy ─────────────────────────────────────────────
    //
    // EDIFACT qualifies every quantity and every price, and only some of those codes
    // mean "this is what was ordered" / "this is the price the line is calculated
    // with". Codes below are from the UN/EDIFACT D96A code lists — DE 6063 (quantity
    // qualifier, composite C186) and DE 5125 (price qualifier, composite C509); D01B
    // keeps the same values for all of them.
    //
    // Anything outside these two sets is still READ, but the line is flagged (see
    // Flush). It is never accepted silently: a QTY qualified 52 is a pack size and a
    // PRI qualified AAE is an information price, and delivering either as if it were
    // the ordered quantity or the invoice price is precisely the silent corruption
    // this parser must not produce.

    /// <summary>
    /// DE 6063 codes that ARE the ordered quantity of the line:
    ///   21 = "Ordered quantity" — the quantity which has been ordered;
    ///   1  = "Discrete quantity" — a single unqualified quantity.
    /// Deliberately nothing else. Every other 6063 code that turns up on an ORDERS line
    /// means a DIFFERENT quantity — 52 quantity per pack, 53/54 minimum/maximum order
    /// quantity, 59 consumer units in the traded unit, 192 free goods quantity, 12
    /// despatch quantity, 46 pieces delivered, 61 return quantity, 3 cumulative, 11
    /// split — so accepting any of them outright would deliver a packaging, constraint
    /// or free-goods number as the quantity ordered.
    /// </summary>
    private static bool IsAcceptedQuantityQualifier(string? qualifier) =>
        string.Equals(qualifier, "21", StringComparison.Ordinal)
        || string.Equals(qualifier, "1", StringComparison.Ordinal);

    /// <summary>
    /// DE 5125 codes that ARE the price the line is calculated with:
    ///   AAA = "Calculation net"   — the net price including allowances/charges;
    ///   AAB = "Calculation gross" — the gross price to which allowances/charges apply;
    ///   CAL = "Calculation price" — "the price for the calculation of the line item amount".
    /// Deliberately nothing else. AAE and AAF ("Information price, excluding allowances
    /// or charges, including / and taxes") and INF are stated for information only, AAC
    /// excludes allowances but includes tax, AAD is an average selling price and INV a
    /// referenced invoice price. Each has a different magnitude from the calculation
    /// price, so none may become the unit price unflagged.
    /// </summary>
    private static bool IsAcceptedPriceQualifier(string? qualifier) =>
        string.Equals(qualifier, "AAA", StringComparison.Ordinal)
        || string.Equals(qualifier, "AAB", StringComparison.Ordinal)
        || string.Equals(qualifier, "CAL", StringComparison.Ordinal);

    /// <summary>
    /// One plain-language sentence per problem, joined. Covers both the locale-ambiguity
    /// case shared with every other parser (via <see cref="NumberParsing"/>) and the
    /// EDIFACT-specific "read, but under a qualifier we cannot treat as authoritative"
    /// case. Null when the line is clean.
    /// </summary>
    private static string? BuildLineReviewReason(
        bool    qtyAmbiguous,
        bool    priceAmbiguous,
        bool    qtyUnconfirmed,
        bool    qtyRead,
        string? qtyQualifier,
        bool    priceUnconfirmed,
        string? priceQualifier)
    {
        var reasons = new List<string>(3);

        if (BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous) is { } ambiguity)
            reasons.Add(ambiguity);

        if (qtyUnconfirmed)
        {
            reasons.Add(
                !qtyRead
                    ? "This line has no quantity segment, so the quantity is 0 — check the order file and enter the quantity ordered."
                : qtyQualifier is null
                    ? "The quantity came from a QTY segment with no qualifier, so it may not be the amount ordered — check it against the order file."
                    : $"The quantity came from QTY qualifier '{qtyQualifier}', not the ordered quantity (qualifier 21 or 1), so it may not be the amount ordered — check it against the order file.");
        }

        if (priceUnconfirmed)
        {
            reasons.Add(
                priceQualifier is null
                    ? "The unit price came from a PRI segment with no qualifier, so it may not be the price to invoice — check it against the order file."
                    : $"The unit price came from PRI qualifier '{priceQualifier}', not a calculation price (AAA, AAB or CAL), so it may not be the price to invoice — check it against the order file.");
        }

        return reasons.Count == 0 ? null : string.Join(" ", reasons);
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
