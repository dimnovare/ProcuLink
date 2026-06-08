using System.Globalization;
using System.Text;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Serializes a canonical <see cref="ParsedOrder"/> into a hand-rolled ANSI ASC
/// X12 850 Purchase Order interchange (version 004010). No third-party EDI
/// library is used — the segment grammar is emitted directly, the mirror image
/// of <see cref="X12OrderParser"/>.
///
/// Envelope (delimiters: <c>*</c> element, <c>&gt;</c> component, <c>~</c> segment):
/// <code>
/// ISA*00*          *00*          *ZZ*PROCULINK      *ZZ*SUPPLIER       *yyMMdd*HHmm*U*00401*000000001*0*P*&gt;~
/// GS*PO*PROCULINK*SUPPLIER*yyyyMMdd*HHmm*1*X*004010~
/// ST*850*0001~
/// BEG*00*NE*{poNumber}**{CCYYMMDD}~
/// CUR*BY*{currency}~
/// N1*BY*{buyerName}~                 (only when BuyerName present)
/// PO1*{n}*{qty}*{uom}*{price}*PE*BP*{buyerItemCode}~
/// PID*F****{description}~            (only when Description present)
/// CTT*{lineCount}~
/// SE*{segmentCount}*0001~
/// GE*1*1~
/// IEA*1*000000001~
/// </code>
///
/// The ISA segment is fixed-width (exactly 105 characters before the terminator)
/// so <see cref="X12OrderParser"/> can recover the delimiters positionally:
/// element separator at index 3, component separator (ISA16) at index 104,
/// segment terminator at index 105. Control numbers (ISA13/IEA02, GS06/GE02,
/// ST02/SE02) and the SE/CTT counts are computed to balance.
/// </summary>
public sealed class X12ParsedOrderTransform : IParsedOrderTransform
{
    private const char ElementSep   = '*';
    private const char ComponentSep = '>';
    private const char SegmentSep   = '~';

    // Single interchange / group / transaction set per document.
    private const string InterchangeControl = "000000001"; // ISA13 / IEA02 (9 digits)
    private const string GroupControl       = "1";          // GS06 / GE02
    private const string StControl          = "0001";       // ST02 / SE02

    private const string SenderId   = "PROCULINK";
    private const string ReceiverId = "SUPPLIER";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.X12_850;

    public ParsedOrderTransformResult Transform(ParsedOrder order, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Required-by-format guard: an empty buyer item code leaves the PO1 BP
        // qualifier with no value (invalid 850), and a missing / zero unit price
        // emits a $0 line. Surfaced as a TransformValidationException so the lines
        // hit /operations/exceptions instead of delivering a broken document. No-op
        // for a well-formed order — emitted bytes are unchanged.
        OutputFieldValidator.ValidateParsedOrder(order, format);

        // Delimiter contamination in a code field (buyer code / unit) has no X12
        // escape; surface it as a review flag rather than silently space-substituting
        // a structured vendor part number.
        X12Sanitizer.GuardCodeFields(order);

        var now       = DateTime.UtcNow;
        var currency  = string.IsNullOrWhiteSpace(order.Currency) ? "USD" : order.Currency.Trim().ToUpperInvariant();
        var poNumber  = string.IsNullOrWhiteSpace(order.PoNumber) ? "UNKNOWN" : order.PoNumber.Trim();
        var orderDate = (order.OrderDate ?? now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();

        // ── ISA — fixed-width interchange control header ───────────────────────
        sb.Append(BuildIsa(now)).Append(SegmentSep);

        // ── GS — functional group header (PO = purchase order) ─────────────────
        AppendSegment(sb, "GS", "PO", SenderId, ReceiverId,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmm", CultureInfo.InvariantCulture),
            GroupControl, "X", "004010");

        // ── Transaction set (ST … SE). Built into a list so SE/CTT can count. ──
        var tx = new List<string>
        {
            Segment("ST", "850", StControl),

            // BEG*00*NE*{po}**{date} — 00 original, NE new order.
            Segment("BEG", "00", "NE", Sanitize(poNumber), "", orderDate),

            // CUR*BY*{currency} — buying party currency.
            Segment("CUR", "BY", currency),
        };

        // N1*BY*{buyerName} — buyer name party loop (required segment in the
        // envelope per task spec; emitted only when a buyer name is known).
        if (!string.IsNullOrWhiteSpace(order.BuyerName))
            tx.Add(Segment("N1", "BY", Sanitize(order.BuyerName)));

        var lineCount = 0;
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            lineCount++;

            // PO1*{n}*{qty}*{uom}*{price}*PE*BP*{buyerItemCode}
            tx.Add(Segment("PO1",
                line.LineNumber.ToString(CultureInfo.InvariantCulture),
                Num(line.Quantity),
                string.IsNullOrWhiteSpace(line.Unit) ? "EA" : Sanitize(line.Unit),
                Num(line.UnitPrice ?? 0m),
                "PE",
                "BP", Sanitize(line.BuyerItemCode)));

            if (!string.IsNullOrWhiteSpace(line.Description))
                tx.Add(Segment("PID", "F", "", "", "", Sanitize(line.Description)));
        }

        // CTT*{lineCount} — transaction totals (number of PO1 line items).
        tx.Add(Segment("CTT", lineCount.ToString(CultureInfo.InvariantCulture)));

        // SE*{segmentCount}*{control} — count includes ST … SE inclusive.
        var segmentCount = tx.Count + 1; // + SE itself
        tx.Add(Segment("SE", segmentCount.ToString(CultureInfo.InvariantCulture), StControl));

        foreach (var seg in tx)
            sb.Append(seg).Append(SegmentSep);

        // ── Trailers ───────────────────────────────────────────────────────────
        AppendSegment(sb, "GE", "1", GroupControl);
        AppendSegment(sb, "IEA", "1", InterchangeControl);

        return new ParsedOrderTransformResult(
            Content:       Encoding.UTF8.GetBytes(sb.ToString()),
            ContentType:   "application/edi-x12",
            FileExtension: ".x12");
    }

    // ── Segment builders ─────────────────────────────────────────────────────

    private static void AppendSegment(StringBuilder sb, string tag, params string[] elements) =>
        sb.Append(Segment(tag, elements)).Append(SegmentSep);

    private static string Segment(string tag, params string[] elements)
    {
        var sb = new StringBuilder(tag);
        foreach (var e in elements)
            sb.Append(ElementSep).Append(e);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the fixed-width ISA segment. Without the trailing terminator the
    /// layout is exactly 105 characters: "ISA" + 16 elements each prefixed by the
    /// element separator. ISA16 (the component separator) lands at index 104, so a
    /// reader recovers the element separator from index 3, the component separator
    /// from index 104, and the segment terminator from index 105.
    /// </summary>
    private static string BuildIsa(DateTime now) =>
        Segment("ISA",
            "00",                                     // ISA01 authorization info qualifier
            new string(' ', 10),                      // ISA02 authorization info
            "00",                                     // ISA03 security info qualifier
            new string(' ', 10),                      // ISA04 security info
            "ZZ",                                     // ISA05 interchange ID qualifier
            SenderId.PadRight(15)[..15],              // ISA06 sender ID
            "ZZ",                                     // ISA07 interchange ID qualifier
            ReceiverId.PadRight(15)[..15],            // ISA08 receiver ID
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), // ISA09 date
            now.ToString("HHmm", CultureInfo.InvariantCulture),   // ISA10 time
            "U",                                      // ISA11 repetition/standards id
            "00401",                                  // ISA12 control version number
            InterchangeControl,                       // ISA13 interchange control number
            "0",                                      // ISA14 acknowledgment requested
            "P",                                      // ISA15 usage indicator (P = production)
            ComponentSep.ToString());                 // ISA16 component element separator

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Strips X12 delimiter characters from a value so it cannot corrupt the segment
    /// structure. X12 has no release/escape mechanism, so substitution is the only
    /// safe option. Identifier fields (buyer code, unit) are guarded up-front by
    /// <see cref="X12Sanitizer.GuardCodeFields(ParsedOrder)"/> so this only ever runs
    /// the substitution on free text in practice.
    /// </summary>
    private static string Sanitize(string? value) => X12Sanitizer.SanitizeFreeText(value);
}
