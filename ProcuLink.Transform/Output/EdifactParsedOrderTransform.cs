using System.Globalization;
using System.Text;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Serializes a canonical <see cref="ParsedOrder"/> into a hand-rolled UN/EDIFACT
/// ORDERS D.96A message. No third-party EDI library is used — the segment grammar
/// is emitted directly, the mirror image of <see cref="EdifactOrderParser"/>.
///
/// Default delimiters per ISO 9735 (no UNA emitted — the defaults are implied):
/// component <c>:</c>, element <c>+</c>, decimal <c>.</c>, release <c>?</c>,
/// segment <c>'</c>.
///
/// Message structure:
/// <code>
/// UNB+UNOC:3+PROCULINK+SUPPLIER+yyMMdd:HHmm+1'
/// UNH+1+ORDERS:D:96A:UN'
/// BGM+220+{poNumber}+9'
/// DTM+137:{CCYYMMDD}:102'
/// NAD+BY+++{buyerName}'              (only when BuyerName present)
/// CUX+2:{currency}:9'
/// LIN+{n}++{buyerItemCode}:IN'
/// IMD+F+ANM+:::{description}'        (only when Description present)
/// QTY+21:{qty}:{unit}'
/// PRI+AAA:{price}'                   (only when UnitPrice present)
/// UNS+S'
/// UNT+{segmentCount}+1'
/// UNZ+1+1'
/// </code>
///
/// <para>
/// The <c>UNT</c> segment count covers every segment from <c>UNH</c> to <c>UNT</c>
/// inclusive, per ISO 9735. Free-text values are release-escaped so a literal
/// delimiter character in a description / name cannot corrupt the structure;
/// <see cref="EdifactOrderParser"/> reverses the escaping on read.
/// </para>
/// </summary>
public sealed class EdifactParsedOrderTransform : IParsedOrderTransform
{
    // EDIFACT default delimiters (ISO 9735).
    private const char Component = ':';
    private const char Element   = '+';
    private const char Release   = '?';
    private const char Segment   = '\'';

    private const string SenderId    = "PROCULINK";
    private const string RecipientId = "SUPPLIER";
    private const string ControlRef  = "1"; // UNB05 / UNH01 / UNT02 / UNZ02

    public bool CanTransform(OutputFormat format) => format == OutputFormat.EdifactOrders;

    public ParsedOrderTransformResult Transform(ParsedOrder order, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Required-by-format guard: an empty buyer item code makes the LIN segment's
        // mandatory item-number component empty (structurally invalid ORDERS), and a
        // missing / zero unit price emits a financially-wrong PRI. Both are surfaced
        // as a TransformValidationException so the lines hit /operations/exceptions
        // instead of delivering a broken document. No-op for a well-formed order.
        OutputFieldValidator.ValidateParsedOrder(order, format);

        var now       = DateTime.UtcNow;
        var currency  = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency.Trim().ToUpperInvariant();
        var poNumber  = string.IsNullOrWhiteSpace(order.PoNumber) ? "UNKNOWN" : order.PoNumber.Trim();
        var orderDate = (order.OrderDate ?? now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();

        // ── UNB — interchange header (not counted by UNT) ──────────────────────
        // UNB+UNOC:3+SENDER+RECIPIENT+yyMMdd:HHmm+ref'
        WriteRaw(sb, "UNB",
            $"UNOC{Component}3",
            Esc(SenderId),
            Esc(RecipientId),
            $"{now:yyMMdd}{Component}{now:HHmm}",
            ControlRef);

        // ── Message body (UNH … UNT) — counted for UNT ─────────────────────────
        var body = new List<string>
        {
            // UNH+ref+ORDERS:D:96A:UN
            Build("UNH", ControlRef, $"ORDERS{Component}D{Component}96A{Component}UN"),

            // BGM+220+{poNumber}+9  (220 = purchase order, 9 = original)
            Build("BGM", "220", Esc(poNumber), "9"),

            // DTM+137:{CCYYMMDD}:102  (137 = document/order date, 102 = CCYYMMDD)
            Build("DTM", $"137{Component}{orderDate}{Component}102"),
        };

        // NAD+BY+++{buyerName}  (BY = buyer; party name composite at element 4)
        if (!string.IsNullOrWhiteSpace(order.BuyerName))
            body.Add(Build("NAD", "BY", "", "", Esc(order.BuyerName.Trim())));

        // CUX+2:{currency}:9  (2 = order currency, 9 = ordering)
        body.Add(Build("CUX", $"2{Component}{currency}{Component}9"));

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            var lineNo   = line.LineNumber.ToString(CultureInfo.InvariantCulture);
            var itemCode = string.IsNullOrWhiteSpace(line.BuyerItemCode) ? string.Empty : Esc(line.BuyerItemCode.Trim());
            var unit     = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : Esc(line.Unit.Trim());

            // LIN+{n}++{itemCode}:IN  (IN = buyer's item number qualifier)
            body.Add(Build("LIN", lineNo, "", $"{itemCode}{Component}IN"));

            // IMD+F+ANM+:::{description}  (description sits at component index 3 of element 3)
            if (!string.IsNullOrWhiteSpace(line.Description))
                body.Add(Build("IMD", "F", "ANM", $"{Component}{Component}{Component}{Esc(line.Description.Trim())}"));

            // QTY+21:{qty}:{unit}  (21 = ordered quantity)
            body.Add(Build("QTY", $"21{Component}{NumInvariant(line.Quantity)}{Component}{unit}"));

            // PRI+AAA:{price}  (AAA = net unit price)
            if (line.UnitPrice is { } price)
                body.Add(Build("PRI", $"AAA{Component}{NumInvariant(price)}"));
        }

        // UNS+S — section separator between detail and summary.
        body.Add(Build("UNS", "S"));

        // UNT+{segmentCount}+{ref} — count is UNH … UNT inclusive (so body.Count + 1).
        var segmentCount = body.Count + 1;
        body.Add(Build("UNT", segmentCount.ToString(CultureInfo.InvariantCulture), ControlRef));

        foreach (var seg in body)
            sb.Append(seg);

        // ── UNZ — interchange trailer (not counted by UNT) ─────────────────────
        WriteRaw(sb, "UNZ", "1", ControlRef);

        return new ParsedOrderTransformResult(
            Content:       Encoding.UTF8.GetBytes(sb.ToString()),
            ContentType:   "application/edifact",
            FileExtension: ".edi");
    }

    // ── Segment builders ─────────────────────────────────────────────────────

    /// <summary>Builds a segment string (tag + elements) terminated by the segment delimiter.</summary>
    private static string Build(string tag, params string[] elements)
    {
        var sb = new StringBuilder(tag);
        foreach (var e in elements)
            sb.Append(Element).Append(e);
        sb.Append(Segment);
        return sb.ToString();
    }

    /// <summary>Appends a segment directly to the output buffer.</summary>
    private static void WriteRaw(StringBuilder sb, string tag, params string[] elements) =>
        sb.Append(Build(tag, elements));

    private static string NumInvariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Release-escapes EDIFACT special characters in a free-text value by prefixing
    /// each with the release character. Mirrors the un-escaping in
    /// <see cref="EdifactOrderParser"/>. The release character itself is escaped first
    /// to avoid double-processing.
    /// </summary>
    private static string Esc(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == Release || c == Component || c == Element || c == Segment)
                sb.Append(Release);
            sb.Append(c);
        }
        return sb.ToString();
    }
}
