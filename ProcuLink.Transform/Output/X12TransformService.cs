using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a valid ANSI ASC X12 850 Purchase Order interchange (version
/// 004010) from a fully-resolved purchase order entity — the North-American
/// counterpart to <see cref="UblOrderTransformService"/> / cXML output.
///
/// Output skeleton (delimiters: <c>*</c> element, <c>&gt;</c> component,
/// <c>~</c> segment):
/// <code>
/// ISA*00*          *00*          *ZZ*PROCULINK      *ZZ*SUPPLIER       *260529*0830*U*00401*000000001*0*P*&gt;~
/// GS*PO*PROCULINK*SUPPLIER*20260529*0830*1*X*004010~
/// ST*850*0001~
/// BEG*00*NE*{poNumber}**{CCYYMMDD}~
/// CUR*BY*{currency}~
/// PO1*1*{qty}*{uom}*{price}*PE*BP*{buyerItemCode}*VP*{supplierItemCode}~
/// PID*F****{description}~
/// CTT*{lineCount}~
/// SE*{segmentCount}*0001~
/// GE*1*1~
/// IEA*1*000000001~
/// </code>
///
/// The ISA segment is fixed-width (105 chars before the terminator) so an X12
/// reader — including our own <see cref="ProcuLink.Transform.Parsing.X12OrderParser"/>
/// — can recover the delimiters positionally. Control numbers (ISA13/IEA02,
/// GS06/GE02, ST02/SE02) and the SE/CTT counts are computed to balance.
///
/// Validation mirrors <see cref="CxmlTransformService"/> / UBL: throws
/// <see cref="TransformValidationException"/> when any line still requires
/// review or is missing a SupplierItemCode.
/// </summary>
public sealed class X12TransformService : ITransformService
{
    private const char ElementSep   = '*';
    private const char ComponentSep = '>';
    private const char SegmentSep   = '~';

    // Control numbers — single interchange / group / transaction set per document.
    private const string InterchangeControl = "000000001"; // ISA13 / IEA02 (9 digits)
    private const string GroupControl       = "1";          // GS06 / GE02
    private const string StControl          = "0001";       // ST02 / SE02

    private const string SenderId   = "PROCULINK";
    private const string ReceiverId = "SUPPLIER";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.X12;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct)
    {
        // Existing review guard + format-required-field checks (empty BP buyer code,
        // missing / zero unit price). Throws TransformValidationException so the lines
        // surface in /operations/exceptions instead of an invalid / $0 850.
        OutputFieldValidator.ValidateEntity(order, format);

        // Delimiter contamination in a code field has no X12 escape — flag it for
        // review rather than silently space-substituting a structured vendor part code.
        X12Sanitizer.GuardCodeFields(order);

        var now      = DateTime.UtcNow;
        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "USD" : order.Currency.ToUpperInvariant();

        var sb = new StringBuilder();

        // ── ISA — fixed-width interchange control header ───────────────────────
        sb.Append(BuildIsa(now)).Append(SegmentSep);

        // ── GS — functional group header (PO = purchase order) ─────────────────
        AppendSegment(sb, "GS", "PO", SenderId, ReceiverId,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmm", CultureInfo.InvariantCulture),
            GroupControl, "X", "004010");

        // ── Transaction set (ST … SE). Built into a list so SE/CTT can count. ──
        var tx = new List<string>();

        tx.Add(Segment("ST", "850", StControl));

        // BEG*00*NE*{po}**{date} — 00 original, NE new order.
        tx.Add(Segment("BEG", "00", "NE", Sanitize(order.PoNumber), "",
            order.OrderDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));

        // CUR*BY*{currency} — buying party currency.
        tx.Add(Segment("CUR", "BY", currency));

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // PO1*{n}*{qty}*{uom}*{price}*PE*BP*{buyer}*VP*{supplier}
            tx.Add(Segment("PO1",
                line.LineNumber.ToString(CultureInfo.InvariantCulture),
                Num(line.Quantity),
                string.IsNullOrWhiteSpace(line.Unit) ? "EA" : Sanitize(line.Unit),
                Num(line.UnitPrice),
                "PE",
                "BP", Sanitize(line.BuyerItemCode),
                "VP", Sanitize(line.SupplierItemCode ?? string.Empty)));

            if (!string.IsNullOrWhiteSpace(line.Description))
                tx.Add(Segment("PID", "F", "", "", "", Sanitize(line.Description)));
        }

        // CTT*{lineCount} — transaction totals (number of PO1 line items).
        tx.Add(Segment("CTT", order.Lines.Count.ToString(CultureInfo.InvariantCulture)));

        // SE*{segmentCount}*{control} — count includes ST … SE inclusive.
        var segmentCount = tx.Count + 1; // + SE itself
        tx.Add(Segment("SE", segmentCount.ToString(CultureInfo.InvariantCulture), StControl));

        foreach (var seg in tx)
            sb.Append(seg).Append(SegmentSep);

        // ── Trailers ───────────────────────────────────────────────────────────
        AppendSegment(sb, "GE", "1", GroupControl);
        AppendSegment(sb, "IEA", "1", InterchangeControl);

        var bytes  = Encoding.UTF8.GetBytes(sb.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "application/edi-x12",
            FileExtension: ".x12"));
    }

    // ── Segment builders ─────────────────────────────────────────────────────

    private static void AppendSegment(StringBuilder sb, string tag, params string[] elements)
    {
        sb.Append(Segment(tag, elements)).Append(SegmentSep);
    }

    private static string Segment(string tag, params string[] elements)
    {
        var sb = new StringBuilder(tag);
        foreach (var e in elements)
            sb.Append(ElementSep).Append(e);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the fixed-width ISA segment. Layout (without the trailing segment
    /// terminator) is exactly 105 characters: "ISA" + 16 elements each prefixed
    /// by the element separator. ISA16 (the component separator) lands at index
    /// 104, so a reader recovers the element separator from index 3, the component
    /// separator from index 104, and the segment terminator from index 105.
    /// </summary>
    private static string BuildIsa(DateTime now)
    {
        var isa = Segment("ISA",
            "00",                                  // ISA01 authorization info qualifier
            new string(' ', 10),                   // ISA02 authorization info
            "00",                                  // ISA03 security info qualifier
            new string(' ', 10),                   // ISA04 security info
            "ZZ",                                  // ISA05 interchange ID qualifier
            SenderId.PadRight(15).Substring(0, 15),// ISA06 sender ID
            "ZZ",                                  // ISA07 interchange ID qualifier
            ReceiverId.PadRight(15).Substring(0, 15), // ISA08 receiver ID
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), // ISA09 date
            now.ToString("HHmm", CultureInfo.InvariantCulture),   // ISA10 time
            "U",                                   // ISA11 repetition/standards id (00401 → U)
            "00401",                               // ISA12 control version number
            InterchangeControl,                    // ISA13 interchange control number
            "0",                                   // ISA14 acknowledgment requested
            "P",                                   // ISA15 usage indicator (P = production)
            ComponentSep.ToString());              // ISA16 component element separator

        return isa;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Num(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Strips X12 delimiter characters from a value so it cannot corrupt the segment
    /// structure. X12 has no release/escape mechanism, so substitution is the only
    /// safe option. Identifier fields are guarded up-front by
    /// <see cref="X12Sanitizer.GuardCodeFields(PurchaseOrderEntity)"/>, so this only
    /// ever runs the substitution on free text in practice.
    /// </summary>
    private static string Sanitize(string value) => X12Sanitizer.SanitizeFreeText(value);
}
