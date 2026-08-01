using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

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
/// N1*ST*{shipToName}~ N3*{street}~ N4*{city}**{postal}*{country}~ PER*OC*…~   (ship-to, optional)
/// N1*BT*{billToName}~ N3*{street}~ N4*{city}**{postal}*{country}~              (bill-to, optional)
/// N1*BY*{buyerName}~                                                            (buyer, name-only, optional)
/// PER*BD*{contactName}*EM*{email}*TE*{phone}~                                   (order contact, optional)
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
    // ── Legacy envelope identity defaults (WS-12 fallback) ─────────────────────
    // These are the historical compile-time constants. When no EnvelopeConfig is
    // supplied (the overwhelming common case today) every read below falls back to
    // these, so the emitted bytes are BYTE-FOR-BYTE identical to the pre-WS-12 output.
    private const char DefaultElementSep   = '*';
    private const char DefaultComponentSep = '>';
    private const char DefaultSegmentSep   = '~';

    private const string DefaultSenderQualifier   = "ZZ";       // ISA05
    private const string DefaultSenderId          = "PROCULINK"; // ISA06
    private const string DefaultReceiverQualifier = "ZZ";       // ISA07
    private const string DefaultReceiverId        = "SUPPLIER";  // ISA08
    private const string DefaultIsaVersion        = "00401";    // ISA12
    private const string DefaultGsVersion         = "004010";   // GS08
    private const string DefaultUsageIndicator    = "P";        // ISA15 (P = production)

    // Control numbers — single interchange / group / transaction set per document.
    private const string InterchangeControl = "000000001"; // ISA13 / IEA02 (9 digits)
    private const string GroupControl       = "1";          // GS06 / GE02
    private const string StControl          = "0001";       // ST02 / SE02

    public bool CanTransform(OutputFormat format) => format == OutputFormat.X12;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: X12 has no cXML Header
        // Default-identity path: no EnvelopeConfig → byte-identical to the legacy constants.
        => TransformAsync(order, format, ct, envelope: null);

    /// <summary>
    /// WS-12 overload: render the 850 with a per-connection <see cref="EnvelopeConfig"/> driving
    /// the ISA/GS envelope IDENTITY (sender/receiver qualifier+id, version, usage indicator) and
    /// the X12 delimiters. Every envelope read falls back to the legacy constant via
    /// <c>envelope?.X12?.Field ?? &lt;default&gt;</c>, so a null envelope (or a null
    /// <see cref="EnvelopeConfig.X12"/>) is BYTE-FOR-BYTE identical to the pre-WS-12 output
    /// (<c>ISA06=PROCULINK</c> / <c>ISA08=SUPPLIER</c>, <c>*&gt;~</c> delimiters).
    /// </summary>
    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        EnvelopeConfig? envelope)
    {
        var env = envelope?.X12;

        // Resolve the active envelope identity + delimiters. Null/absent → legacy constants.
        var elementSep   = env?.ElementSeparator   ?? DefaultElementSep;
        var componentSep = env?.ComponentSeparator ?? DefaultComponentSep;
        var segmentSep   = env?.SegmentSeparator   ?? DefaultSegmentSep;

        var senderQual   = Blank(env?.IsaSenderQualifier,   DefaultSenderQualifier);
        var senderId     = Blank(env?.IsaSenderId,          DefaultSenderId);
        var receiverQual = Blank(env?.IsaReceiverQualifier, DefaultReceiverQualifier);
        var receiverId   = Blank(env?.IsaReceiverId,        DefaultReceiverId);
        var gsVersion    = Blank(env?.Version,              DefaultGsVersion);
        var isaVersion   = DeriveIsaVersion(env?.Version);
        var usage        = Blank(env?.UsageIndicator,       DefaultUsageIndicator);

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
        sb.Append(BuildIsa(now, elementSep, componentSep,
                senderQual, senderId, receiverQual, receiverId, isaVersion, usage))
          .Append(segmentSep);

        // ── GS — functional group header (PO = purchase order) ─────────────────
        AppendSegment(sb, segmentSep, elementSep, "GS", "PO", senderId, receiverId,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmm", CultureInfo.InvariantCulture),
            GroupControl, "X", gsVersion);

        // ── Transaction set (ST … SE). Built into a list so SE/CTT can count. ──
        var tx = new List<string>();

        tx.Add(Segment(elementSep, "ST", "850", StControl));

        // BEG*00*NE*{po}**{date} — 00 original, NE new order.
        tx.Add(Segment(elementSep, "BEG", "00", "NE", Sanitize(order.PoNumber), "",
            order.OrderDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));

        // CUR*BY*{currency} — buying party currency.
        tx.Add(Segment(elementSep, "CUR", "BY", currency));

        // ── N1 loop (ship-to / bill-to / buyer) + order contact ────────────────
        // Inserted AFTER CUR and BEFORE the first PO1 (the X12 850 heading area). Every block is
        // gated on its source NAME (contact on any of its 3 fields), so an order with no address /
        // contact / buyer data appends ZERO segments here — SE/CTT stay byte-identical to the
        // pre-feature baseline. Every free-text value is delimiter-sanitized (Sanitize); X12 has no
        // escape mechanism so a delimiter must be space-substituted, never allowed into the structure.
        var n1LoopStart = tx.Count;
        AppendN1Address(tx, elementSep, "ST",
            order.ShipToName, order.ShipToStreet, order.ShipToCity, order.ShipToPostalCode,
            order.ShipToCountry, order.ShipToDeliverTo, order.ShipToEmail, order.ShipToPhone);
        AppendN1Address(tx, elementSep, "BT",
            order.BillToName, order.BillToStreet, order.BillToCity, order.BillToPostalCode,
            order.BillToCountry, order.BillToDeliverTo, order.BillToEmail, order.BillToPhone);

        // Buyer N1*BY is name-only (the canonical model carries no buyer postal address).
        var buyerName = OrderHeaderReader.ExtractBuyerName(order);
        if (!string.IsNullOrWhiteSpace(buyerName))
            tx.Add(Segment(elementSep, "N1", "BY", Sanitize(buyerName)));

        // Order-level ordering contact: PER*BD. PER in the 850 heading is only valid INSIDE an N1
        // loop, so it's emitted only when at least one N1 (ship-to / bill-to / buyer) was actually
        // appended above — otherwise a contact-only order would emit an orphan heading PER that a
        // strict validator rejects. (Realistically the buyer name is always present, so the PER hangs
        // off the N1*BY loop, matching the reference output.)
        var anyN1Emitted = tx.Count > n1LoopStart;
        if (anyN1Emitted
            && (!string.IsNullOrWhiteSpace(order.ContactName)
                || !string.IsNullOrWhiteSpace(order.ContactEmail)
                || !string.IsNullOrWhiteSpace(order.ContactPhone)))
        {
            tx.Add(Segment(elementSep, "PER", "BD",
                Sanitize(order.ContactName ?? string.Empty),
                "EM", Sanitize(order.ContactEmail ?? string.Empty),
                "TE", Sanitize(order.ContactPhone ?? string.Empty)));
        }

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // PO1*{n}*{qty}*{uom}*{price}*PE*BP*{buyer}*VP*{supplier}
            tx.Add(Segment(elementSep, "PO1",
                line.LineNumber.ToString(CultureInfo.InvariantCulture),
                Num(line.Quantity),
                string.IsNullOrWhiteSpace(line.Unit) ? "EA" : Sanitize(line.Unit),
                Num(line.UnitPrice),
                "PE",
                "BP", Sanitize(line.BuyerItemCode),
                "VP", Sanitize(line.SupplierItemCode ?? string.Empty)));

            if (!string.IsNullOrWhiteSpace(line.Description))
                tx.Add(Segment(elementSep, "PID", "F", "", "", "", Sanitize(line.Description)));
        }

        // CTT*{lineCount} — transaction totals (number of PO1 line items).
        tx.Add(Segment(elementSep, "CTT", order.Lines.Count.ToString(CultureInfo.InvariantCulture)));

        // SE*{segmentCount}*{control} — count includes ST … SE inclusive.
        var segmentCount = tx.Count + 1; // + SE itself
        tx.Add(Segment(elementSep, "SE", segmentCount.ToString(CultureInfo.InvariantCulture), StControl));

        foreach (var seg in tx)
            sb.Append(seg).Append(segmentSep);

        // ── Trailers ───────────────────────────────────────────────────────────
        AppendSegment(sb, segmentSep, elementSep, "GE", "1", GroupControl);
        AppendSegment(sb, segmentSep, elementSep, "IEA", "1", InterchangeControl);

        var bytes  = Encoding.UTF8.GetBytes(sb.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(TransformResult.For(OutputFormat.X12, stream));
    }

    // ── N1 address loop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Appends one X12 N1 loop (party + address + per-party contact) to <paramref name="tx"/>, gated
    /// on a non-blank <paramref name="name"/> (a name-less party emits NOTHING → byte-identical when
    /// absent). Segments: <c>N1*{qual}*{name}</c>; <c>N3*{street}</c> when a street is present;
    /// <c>N4*{city}**{postal}*{country}</c> when any of city/postal/country is present (the empty
    /// element is N402, the state/province — left blank); and a <c>PER*OC</c> (order contact) carrying
    /// the per-party deliver-to + email + phone when an email or phone is present. Every free-text
    /// element is delimiter-sanitized — X12 has no escape, so a stray delimiter is space-substituted.
    /// </summary>
    private static void AppendN1Address(
        List<string> tx, char elementSep, string qualifier,
        string? name, string? street, string? city, string? postal, string? country,
        string? deliverTo, string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        tx.Add(Segment(elementSep, "N1", qualifier, Sanitize(name)));

        if (!string.IsNullOrWhiteSpace(street))
            tx.Add(Segment(elementSep, "N3", Sanitize(street)));

        if (!string.IsNullOrWhiteSpace(city)
            || !string.IsNullOrWhiteSpace(postal)
            || !string.IsNullOrWhiteSpace(country))
        {
            // N4*{city}*{state}*{postal}*{country} — state (N402) intentionally left empty.
            tx.Add(Segment(elementSep, "N4",
                Sanitize(city ?? string.Empty),
                string.Empty,
                Sanitize(postal ?? string.Empty),
                Sanitize(country ?? string.Empty)));
        }

        if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(phone))
        {
            // PER*OC (order contact) — carries the per-party deliver-to contact + email + phone.
            tx.Add(Segment(elementSep, "PER", "OC",
                Sanitize(deliverTo ?? string.Empty),
                "EM", Sanitize(email ?? string.Empty),
                "TE", Sanitize(phone ?? string.Empty)));
        }
    }

    // ── Segment builders ─────────────────────────────────────────────────────

    private static void AppendSegment(
        StringBuilder sb, char segmentSep, char elementSep, string tag, params string[] elements)
    {
        sb.Append(Segment(elementSep, tag, elements)).Append(segmentSep);
    }

    private static string Segment(char elementSep, string tag, params string[] elements)
    {
        var sb = new StringBuilder(tag);
        foreach (var e in elements)
            sb.Append(elementSep).Append(e);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the fixed-width ISA segment. Layout (without the trailing segment
    /// terminator) is exactly 105 characters: "ISA" + 16 elements each prefixed
    /// by the element separator. ISA16 (the component separator) lands at index
    /// 104, so a reader recovers the element separator from index 3, the component
    /// separator from index 104, and the segment terminator from index 105.
    /// </summary>
    private static string BuildIsa(
        DateTime now, char elementSep, char componentSep,
        string senderQual, string senderId, string receiverQual, string receiverId,
        string isaVersion, string usage)
    {
        var isa = Segment(elementSep, "ISA",
            "00",                                  // ISA01 authorization info qualifier
            new string(' ', 10),                   // ISA02 authorization info
            "00",                                  // ISA03 security info qualifier
            new string(' ', 10),                   // ISA04 security info
            senderQual.PadRight(2).Substring(0, 2),    // ISA05 interchange ID qualifier
            senderId.PadRight(15).Substring(0, 15),    // ISA06 sender ID
            receiverQual.PadRight(2).Substring(0, 2),  // ISA07 interchange ID qualifier
            receiverId.PadRight(15).Substring(0, 15),  // ISA08 receiver ID
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), // ISA09 date
            now.ToString("HHmm", CultureInfo.InvariantCulture),   // ISA10 time
            "U",                                   // ISA11 repetition/standards id (00401 → U)
            isaVersion.PadRight(5).Substring(0, 5),// ISA12 control version number
            InterchangeControl,                    // ISA13 interchange control number
            "0",                                   // ISA14 acknowledgment requested
            usage.PadRight(1).Substring(0, 1),     // ISA15 usage indicator (P = production / T = test)
            componentSep.ToString());              // ISA16 component element separator

        return isa;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Returns the configured value when it is non-blank, otherwise the legacy default.</summary>
    private static string Blank(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    /// <summary>
    /// Maps the GS08-style version (<c>004010</c>) to the 5-char ISA12 control version (<c>00401</c>).
    /// A null/blank/legacy version returns the legacy <c>00401</c> so the ISA stays byte-identical.
    /// A custom GS version that is exactly the legacy <c>004010</c> also keeps <c>00401</c>; any other
    /// value is truncated to 5 chars for the fixed-width ISA12 slot.
    /// </summary>
    private static string DeriveIsaVersion(string? gsVersion)
    {
        if (string.IsNullOrWhiteSpace(gsVersion) || gsVersion.Trim() == DefaultGsVersion)
            return DefaultIsaVersion;
        var v = gsVersion.Trim();
        return v.Length >= 5 ? v.Substring(0, 5) : v.PadRight(5);
    }

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
