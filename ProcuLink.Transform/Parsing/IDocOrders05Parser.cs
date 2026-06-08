using System.Globalization;
using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses SAP IDoc <c>ORDERS05</c> purchase orders (basic type ORDERS05,
/// message type ORDERS) into a <see cref="ParsedOrder"/>. These are emitted by
/// the customer's SAP system and arrive as plain <c>.xml</c>; the wedge customer
/// base (Baltic IT distributors) receives many of them.
///
/// Supported structure (one IDOC per file; the first IDOC is used):
/// <code>
/// &lt;ORDERS05&gt;
///   &lt;IDOC BEGIN="1"&gt;
///     &lt;E1EDK01&gt;&lt;CURCY&gt;EUR&lt;/CURCY&gt;&lt;BELNR&gt;4501450099&lt;/BELNR&gt;&lt;/E1EDK01&gt;   &lt;!-- header --&gt;
///     &lt;E1EDKA1&gt;&lt;PARVW&gt;AG&lt;/PARVW&gt;&lt;ORGTX&gt;Acme Buyer&lt;/ORGTX&gt;&lt;/E1EDKA1&gt;     &lt;!-- AG=buyer, WE=ship-to, LF=supplier --&gt;
///     &lt;E1EDK02&gt;&lt;QUALF&gt;001&lt;/QUALF&gt;&lt;BELNR&gt;4501450099&lt;/BELNR&gt;&lt;DATUM&gt;20260603&lt;/DATUM&gt;&lt;/E1EDK02&gt;
///     &lt;E1EDP01&gt;                                                    &lt;!-- one per line --&gt;
///       &lt;POSEX&gt;00010&lt;/POSEX&gt;&lt;MENGE&gt;50.000&lt;/MENGE&gt;&lt;MENEE&gt;EA&lt;/MENEE&gt;
///       &lt;VPREI&gt;1.05&lt;/VPREI&gt;&lt;NETWR&gt;52.5&lt;/NETWR&gt;
///       &lt;E1EDP19&gt;&lt;QUALF&gt;002&lt;/QUALF&gt;&lt;IDTNR&gt;15728463&lt;/IDTNR&gt;&lt;/E1EDP19&gt;  &lt;!-- 002 = buyer material no. --&gt;
///       &lt;E1EDP19&gt;&lt;QUALF&gt;001&lt;/QUALF&gt;&lt;KTEXT&gt;Short text&lt;/KTEXT&gt;&lt;/E1EDP19&gt;
///       &lt;E1EDPT1&gt;&lt;E1EDPT2&gt;&lt;TDLINE&gt;Long description line 1&lt;/TDLINE&gt;&lt;/E1EDPT2&gt;...&lt;/E1EDPT1&gt;
///     &lt;/E1EDP01&gt;
///     &lt;E1EDS01&gt;&lt;SUMME&gt;186.01&lt;/SUMME&gt;&lt;SUNIT&gt;EUR&lt;/SUNIT&gt;&lt;/E1EDS01&gt;  &lt;!-- summary --&gt;
///   &lt;/IDOC&gt;
/// &lt;/ORDERS05&gt;
/// </code>
///
/// Element resolution is namespace-agnostic, matching by local-name comparison
/// (same approach as <see cref="CxmlOrderParser"/> / <see cref="UblOrderParser"/>),
/// so IDocs with or without an envelope namespace parse uniformly.
///
/// Field mapping:
/// <list type="bullet">
/// <item>PO number — <c>E1EDK02 BELNR</c> where <c>QUALF=001</c>, falling back to <c>E1EDK01 BELNR</c>.</item>
/// <item>Currency — <c>E1EDS01 SUNIT</c> (the ISO code); <c>E1EDK01 CURCY</c> is often a numeric
///   internal code (e.g. <c>704</c>) so it is used only when alphabetic.</item>
/// <item>Order date — <c>E1EDK02 DATUM</c> (<c>yyyyMMdd</c>).</item>
/// <item>Grand total — <c>E1EDS01 SUMME</c>.</item>
/// <item>Buyer name — <c>E1EDKA1</c> with <c>PARVW=AG</c>; supplier name — <c>PARVW=LF</c>
///   (<c>WE</c> = ship-to has no canonical home and is ignored).</item>
/// <item>Lines — <c>E1EDP01</c>: <c>POSEX</c>→line no., <c>MENGE</c>→qty, <c>MENEE</c>→unit,
///   <c>VPREI</c>→unit price, <c>NETWR</c>→line amount. Buyer item code from <c>E1EDP19 IDTNR</c>
///   (preferring <c>QUALF=002</c> buyer material number, then <c>QUALF=001</c>); description from
///   <c>E1EDPT1/E1EDPT2 TDLINE</c> continuation lines (concatenated), falling back to <c>E1EDP19 KTEXT</c>.</item>
/// </list>
///
/// Hand-rolled with <see cref="System.Xml.Linq"/> — no commercial EDI library
/// (EdiFabric is off the table per founder policy).
///
/// Validation: throws <see cref="IDocParseException"/> when the document is not an
/// ORDERS05 IDoc, when the PO number is absent, or when no line segment is present.
/// </summary>
public sealed class IDocOrders05Parser : IPurchaseOrderParser
{
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".xml", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, ct);
        }
        catch (Exception ex)
        {
            throw new IDocParseException($"IDoc document could not be parsed: {ex.Message}", ex);
        }

        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ORDERS05", StringComparison.OrdinalIgnoreCase))
            throw new IDocParseException("Document root element is not <ORDERS05>.");

        // One ORDERS05 envelope can technically batch several IDOCs; we map the first
        // to a single ParsedOrder (these files carry exactly one).
        var idoc = root.Descendants().FirstOrDefault(e =>
                       string.Equals(e.Name.LocalName, "IDOC", StringComparison.OrdinalIgnoreCase))
                   ?? throw new IDocParseException("Required <IDOC> segment is missing.");

        var edk01 = Children(idoc, "E1EDK01").FirstOrDefault();
        var eds01 = Children(idoc, "E1EDS01").FirstOrDefault();

        // ── PO number: E1EDK02 (QUALF=001) BELNR → E1EDK01 BELNR ───────────────
        var edk02List = Children(idoc, "E1EDK02").ToList();
        var poEdk02 = edk02List.FirstOrDefault(e => ChildValue(e, "QUALF") == "001")
                      ?? edk02List.FirstOrDefault(e => !string.IsNullOrWhiteSpace(ChildValue(e, "BELNR")));

        var poNumber = NullIfEmpty(ChildValue(poEdk02, "BELNR"))
                       ?? NullIfEmpty(ChildValue(edk01, "BELNR"));
        if (poNumber is null)
            throw new IDocParseException(
                "Required PO number is missing (no E1EDK02 BELNR with QUALF=001 and no E1EDK01 BELNR).");

        // ── Order date: E1EDK02 DATUM (yyyyMMdd) ───────────────────────────────
        var orderDate = ParseIdocDate(ChildValue(poEdk02, "DATUM"));

        // ── Currency: E1EDS01 SUNIT, else E1EDK01 CURCY when alphabetic ────────
        var currency = NullIfEmpty(ChildValue(eds01, "SUNIT"));
        if (currency is null)
        {
            var curcy = ChildValue(edk01, "CURCY");
            if (!string.IsNullOrWhiteSpace(curcy) && curcy.All(char.IsLetter))
                currency = curcy;
        }

        // ── Grand total: E1EDS01 SUMME ─────────────────────────────────────────
        var grandTotal = ParseDecimal(ChildValue(eds01, "SUMME"));

        // ── Parties: AG=buyer, LF=supplier (WE=ship-to ignored) ────────────────
        var ka1 = Children(idoc, "E1EDKA1").ToList();
        var buyerName = ExtractPartyName(ka1, "AG");
        var supplierName = ExtractPartyName(ka1, "LF");

        // ── Lines: one per E1EDP01 ─────────────────────────────────────────────
        var lineEls = Children(idoc, "E1EDP01").ToList();
        if (lineEls.Count == 0)
            throw new IDocParseException("At least one E1EDP01 line segment is required.");

        var lines = new List<ParsedOrderLine>(lineEls.Count);
        int autoLine = 1;

        foreach (var lineEl in lineEls)
        {
            var posex = ChildValue(lineEl, "POSEX");
            var lineNumber = int.TryParse(posex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln)
                ? ln
                : autoLine;

            var quantity = ParseDecimal(ChildValue(lineEl, "MENGE")) ?? 0m;
            var unit = NullIfEmpty(ChildValue(lineEl, "MENEE"));
            var unitPrice = ParseDecimal(ChildValue(lineEl, "VPREI"));
            var lineAmount = ParseDecimal(ChildValue(lineEl, "NETWR"));

            var idp19s = Children(lineEl, "E1EDP19").ToList();

            // Buyer item code: prefer QUALF=002 (buyer material no.), then QUALF=001,
            // then any IDTNR, then POSEX so a line always carries an identifier.
            var buyerItemCode = FirstIdtnr(idp19s, "002")
                                ?? FirstIdtnr(idp19s, "001")
                                ?? idp19s.Select(e => NullIfEmpty(ChildValue(e, "IDTNR")))
                                         .FirstOrDefault(v => v is not null)
                                ?? NullIfEmpty(posex);
            if (string.IsNullOrWhiteSpace(buyerItemCode))
                throw new IDocParseException(
                    $"E1EDP01 line POSEX={posex ?? "?"} has no usable item identifier "
                  + "(E1EDP19 IDTNR or POSEX required).");

            // Description: concatenated E1EDPT1/E1EDPT2 TDLINEs (the full text),
            // falling back to the truncated E1EDP19 KTEXT short text.
            var description = BuildLineText(lineEl)
                              ?? idp19s.Select(e => NullIfEmpty(ChildValue(e, "KTEXT")))
                                       .FirstOrDefault(v => v is not null);

            lines.Add(new ParsedOrderLine(
                LineNumber: lineNumber,
                BuyerItemCode: buyerItemCode!,
                Description: description,
                Quantity: quantity,
                Unit: unit,
                UnitPrice: unitPrice,
                LineAmount: lineAmount));

            autoLine++;
        }

        return new ParsedOrder(
            PoNumber: poNumber,
            OrderDate: orderDate,
            BuyerName: buyerName,
            Currency: NullIfEmpty(currency?.ToUpperInvariant()),
            Lines: lines,
            SupplierName: supplierName,
            GrandTotal: grandTotal,
            DocumentType: "order");
    }

    // ── Public static helper (factory-friendly content detection) ───────────────

    /// <summary>
    /// Peeks an XML stream to determine whether it is a SAP IDoc ORDERS05 document
    /// (root local-name <c>ORDERS05</c>). Used by <see cref="OrderParserFactory"/> to
    /// disambiguate IDoc vs cXML vs UBL for <c>.xml</c> uploads. Restores the stream
    /// position and returns <c>false</c> on any error rather than throwing — this is
    /// a probe, not a validation pass.
    /// </summary>
    public static bool IsIdocOrders05Document(Stream stream)
    {
        if (stream is null) return false;

        var originalPosition = stream.CanSeek ? stream.Position : -1L;
        try
        {
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                CloseInput = false,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = System.Xml.DtdProcessing.Prohibit
            });

            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                return string.Equals(reader.LocalName, "ORDERS05", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (stream.CanSeek && originalPosition >= 0)
                stream.Position = originalPosition;
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a party name from the E1EDKA1 segment with the given partner role
    /// (<paramref name="parvw"/>). Priority: concatenated NAME1..NAME4 → ORGTX → BNAME.
    /// Returns null when the segment is absent or carries no name fields (the IDoc
    /// often identifies the supplier/LF by partner number only).
    /// </summary>
    private static string? ExtractPartyName(IEnumerable<XElement> ka1Segments, string parvw)
    {
        var seg = ka1Segments.FirstOrDefault(e =>
            string.Equals(ChildValue(e, "PARVW"), parvw, StringComparison.OrdinalIgnoreCase));
        if (seg is null) return null;

        var nameParts = new[] { "NAME1", "NAME2", "NAME3", "NAME4" }
            .Select(n => ChildValue(seg, n))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());
        var name = string.Join(" ", nameParts);
        if (!string.IsNullOrWhiteSpace(name)) return name;

        return NullIfEmpty(ChildValue(seg, "ORGTX")) ?? NullIfEmpty(ChildValue(seg, "BNAME"));
    }

    /// <summary>
    /// Returns the IDTNR of the first E1EDP19 sub-segment whose QUALF matches, or null.
    /// </summary>
    private static string? FirstIdtnr(IEnumerable<XElement> idp19Segments, string qualf) =>
        idp19Segments
            .Where(e => string.Equals(ChildValue(e, "QUALF"), qualf, StringComparison.OrdinalIgnoreCase))
            .Select(e => NullIfEmpty(ChildValue(e, "IDTNR")))
            .FirstOrDefault(v => v is not null);

    /// <summary>
    /// Concatenates the line's long-text continuation lines (E1EDPT1/E1EDPT2/TDLINE)
    /// into a single space-joined string, or null when no line text is present.
    /// </summary>
    private static string? BuildLineText(XElement lineEl)
    {
        var parts = new List<string>();
        foreach (var pt1 in Children(lineEl, "E1EDPT1"))
            foreach (var pt2 in Children(pt1, "E1EDPT2"))
            {
                var tdline = ChildValue(pt2, "TDLINE");
                if (!string.IsNullOrWhiteSpace(tdline))
                    parts.Add(tdline);
            }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Direct children of <paramref name="parent"/> with the given local name (namespace-agnostic).</summary>
    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent is null
            ? Enumerable.Empty<XElement>()
            : parent.Elements().Where(e =>
                string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trimmed value of the first direct child with the given local name, or null.</summary>
    private static string? ChildValue(XElement? parent, string localName) =>
        Children(parent, localName).FirstOrDefault()?.Value?.Trim();

    private static DateTime? ParseIdocDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var dt)
            ? dt
            : null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        // IDoc decimals use '.' as the decimal point (e.g. "1.000" = 1, "149.49").
        // Mirror the sibling parsers: only treat ',' as a decimal point when no '.' is present.
        if (normalized.Contains(',') && !normalized.Contains('.'))
            normalized = normalized.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
