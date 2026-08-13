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
/// <item>Buyer name — <c>E1EDKA1</c> with <c>PARVW=AG</c>; supplier name — <c>PARVW=LF</c>.
///   <c>PARVW=WE</c> (ship-to) and <c>PARVW=RE</c> (bill-to) become <see cref="ParsedParty"/>
///   rows, which the ingestion layer denormalises onto the ShipTo*/BillTo* columns the
///   cXML / UBL / X12 writers emit from.</item>
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
            doc = DtdSafeXmlLoader.Load(fileStream);   // DOCTYPE-tolerant, XXE-safe
            ct.ThrowIfCancellationRequested();
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

        // ── Requested delivery date: E1EDK03 IDDAT=012 DATUM (yyyyMMdd) ────────
        // Peppol BIS 3.0 mandatory; UBL cbc:RequestedDeliveryDate; EDIFACT DTM+2.
        // Read the first E1EDK03 segment with IDDAT=012 (delivery schedule date).
        var edk03List = Children(idoc, "E1EDK03").ToList();
        var deliveryDateSeg = edk03List
            .FirstOrDefault(e => ChildValue(e, "IDDAT") == "012");
        var requestedDeliveryDate = ParseIdocDateOnly(ChildValue(deliveryDateSeg, "DATUM"));

        // ── Currency: E1EDS01 SUNIT, else E1EDK01 CURCY when alphabetic ────────
        var currency = NullIfEmpty(ChildValue(eds01, "SUNIT"));
        if (currency is null)
        {
            var curcy = ChildValue(edk01, "CURCY");
            if (!string.IsNullOrWhiteSpace(curcy) && curcy.All(char.IsLetter))
                currency = curcy;
        }

        // ── Grand total: E1EDS01 SUMME ─────────────────────────────────────────
        // Header total is not line-level so it carries no per-line review flag; the
        // ambiguity bit is discarded here (a header total never gates a line's review).
        var (grandTotal, _) = ParseDecimal(ChildValue(eds01, "SUMME"));

        // ── Parties: AG=buyer, LF=supplier, WE=ship-to, RE=bill-to ─────────────
        var ka1 = Children(idoc, "E1EDKA1").ToList();
        var buyerName = ExtractPartyName(ka1, "AG");
        var supplierName = ExtractPartyName(ka1, "LF");

        // WE / RE carry the addresses the emitters need. Only these two roles are emitted:
        // the ingestion layer denormalises exactly shipTo/billTo onto the flat columns, and
        // AG / LF already have canonical homes in BuyerName / SupplierName above.
        var parties = new List<ParsedParty>(2);
        var shipToParty = BuildParty(ka1, "WE", "shipTo");
        if (shipToParty is not null) parties.Add(shipToParty);
        var billToParty = BuildParty(ka1, "RE", "billTo");
        if (billToParty is not null) parties.Add(billToParty);

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

            var (quantityVal, qtyAmbiguous) = ParseDecimal(ChildValue(lineEl, "MENGE"));
            var quantity = quantityVal ?? 0m;
            var unit = NullIfEmpty(ChildValue(lineEl, "MENEE"));
            var (unitPrice, priceAmbiguous) = ParseDecimal(ChildValue(lineEl, "VPREI"));
            // NETWR (line amount) is a derived total, not a price the buyer keys; it is
            // not folded into NeedsReview (matching X12/cXML, which flag only qty + unit
            // price). Its ambiguity bit is discarded.
            var (lineAmount, _) = ParseDecimal(ChildValue(lineEl, "NETWR"));

            // V5: per-line delivery date from E1EDP20 EDATU (yyyyMMdd).
            // Present on every line in the real fixture corpus (idoc-orders05-11/-710).
            var edp20 = Children(lineEl, "E1EDP20").FirstOrDefault();
            var lineDeliveryDate = ParseIdocDateOnly(ChildValue(edp20, "EDATU"));

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
                LineAmount: lineAmount,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the parser
                // could not read unambiguously flags the line for human review. Mirrors
                // CsvOrderParser/EdifactOrderParser/X12OrderParser's NeedsReview/ReviewReason contract.
                NeedsReview: qtyAmbiguous || priceAmbiguous,
                ReviewReason: NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous),
                // V5: populate per-line delivery date from E1EDP20 EDATU.
                DeliveryDate: lineDeliveryDate));

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
            DocumentType: "order",
            // V5: header-level requested delivery date from E1EDK03 IDDAT=012.
            RequestedDeliveryDate: requestedDeliveryDate,
            Parties: parties.Count > 0 ? parties : null);
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
    /// Maps the E1EDKA1 partner segment with the given SAP partner role
    /// (<paramref name="parvw"/> — <c>WE</c> ship-to, <c>RE</c> bill-to) onto a
    /// <see cref="ParsedParty"/> carrying the address SAP states alongside the name:
    /// <c>STRAS</c> street, <c>ORT01</c> city, <c>PSTLZ</c> postal code, <c>LAND1</c> country,
    /// <c>TELF1</c> phone, <c>BNAME</c> contact person, <c>ILNNR</c> the partner's ILN/GLN.
    /// Returns null when the segment is absent or nameless — a nameless party reaches no
    /// ShipTo*/BillTo* column, because the ingestion layer keys the denormalisation on the name.
    /// </summary>
    private static ParsedParty? BuildParty(IEnumerable<XElement> ka1Segments, string parvw, string role)
    {
        var seg = ka1Segments.FirstOrDefault(e =>
            string.Equals(ChildValue(e, "PARVW"), parvw, StringComparison.OrdinalIgnoreCase));
        if (seg is null) return null;

        var name = ExtractPartyName(new[] { seg }, parvw);
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new ParsedParty(
            Role:        role,
            Name:        name,
            Street:      NullIfEmpty(ChildValue(seg, "STRAS")),
            City:        NullIfEmpty(ChildValue(seg, "ORT01")),
            PostalCode:  NullIfEmpty(ChildValue(seg, "PSTLZ")),
            Country:     NullIfEmpty(ChildValue(seg, "LAND1")),
            // STCD1 is the SAP tax number 1 field; PARTN/LIFNR is the partner number the
            // buyer's SAP knows this address by, which is the closest thing to a reference.
            Vat:         NullIfEmpty(ChildValue(seg, "STCD1")),
            EdiCode:     NullIfEmpty(ChildValue(seg, "ILNNR")),
            Reference:   NullIfEmpty(ChildValue(seg, "PARTN")) ?? NullIfEmpty(ChildValue(seg, "LIFNR")),
            ContactName: NullIfEmpty(ChildValue(seg, "BNAME")),
            Phone:       NullIfEmpty(ChildValue(seg, "TELF1")));
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

    /// <summary>
    /// Parses an IDoc date string (<c>yyyyMMdd</c>) into a <see cref="DateOnly"/>.
    /// V5: used for requested delivery dates (header E1EDK03 DATUM and line E1EDP20 EDATU).
    /// </summary>
    private static DateOnly? ParseIdocDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    /// <summary>
    /// Parse a SAP IDoc numeric value via the shared locale-aware reader. SAP IDoc
    /// emits invariant-locale numbers ('.' decimal, ',' groups thousands), so
    /// <c>european: false</c>. Returns <c>(value, ambiguous)</c>; the caller flags the line
    /// for review when the token could not be read unambiguously rather than emitting a
    /// silently-wrong number. This replaces the old "swap ',' for '.' only when no '.'
    /// present" reader that read EU "1.234,56" as 1.23456 (group dropped) or null and never
    /// flagged the corruption — <c>european: false</c> still reads "1.234,56" → 1234.56 via
    /// the both-separators-present last-wins rule.
    /// </summary>
    private static (decimal? Value, bool Ambiguous) ParseDecimal(string? value) =>
        NumberParsing.TryParseFlexibleDecimal(value, european: false);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
