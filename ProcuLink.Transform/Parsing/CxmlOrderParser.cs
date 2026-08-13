using System.Globalization;
using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses cXML 1.2 purchase-order documents into a <see cref="ParsedOrder"/>.
///
/// Supported structure:
/// <code>
/// &lt;cXML ...&gt;
///   &lt;Header&gt;...&lt;/Header&gt;
///   &lt;Request deploymentMode="production"&gt;
///     &lt;OrderRequest&gt;
///       &lt;OrderRequestHeader orderID="PO-123" orderDate="2024-01-15" type="new"&gt;
///         &lt;Total&gt;&lt;Money currency="EUR"&gt;1250.00&lt;/Money&gt;&lt;/Total&gt;
///       &lt;/OrderRequestHeader&gt;
///       &lt;ItemOut quantity="10" lineNumber="1"&gt;
///         &lt;ItemID&gt;&lt;SupplierPartID&gt;SUP-ABC&lt;/SupplierPartID&gt;&lt;/ItemID&gt;
///         &lt;ItemDetail&gt;
///           &lt;UnitPrice&gt;&lt;Money currency="EUR"&gt;125.00&lt;/Money&gt;&lt;/UnitPrice&gt;
///           &lt;Description xml:lang="en"&gt;Widget&lt;/Description&gt;
///           &lt;UnitOfMeasure&gt;EA&lt;/UnitOfMeasure&gt;
///         &lt;/ItemDetail&gt;
///       &lt;/ItemOut&gt;
///     &lt;/OrderRequest&gt;
///   &lt;/Request&gt;
/// &lt;/cXML&gt;
/// </code>
///
/// Handles both namespaced (http://www.cxml.org/cXML) and bare cXML elements.
/// Registered in DI for extensions <c>.xml</c> and <c>.cxml</c>; takes priority
/// over plain <c>XmlTransformService</c> when the root element is &lt;cXML&gt;.
///
/// Validation: throws <see cref="CxmlParseException"/> when required fields are absent.
/// Required: orderID, deploymentMode, at least one ItemOut with SupplierPartID, Quantity, UnitPrice/Money.
/// </summary>
public sealed class CxmlOrderParser : IPurchaseOrderParser
{
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".cxml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileExtension, ".xml",  StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            // Real-world cXML (Coupa, Ariba, Jaggaer, …) ALWAYS carries a
            // <!DOCTYPE cXML SYSTEM "http://xml.cxml.org/schemas/cXML/.../cXML.dtd"> header.
            // XDocument's default reader uses DtdProcessing.Prohibit and throws
            // "For security reasons DTD is prohibited" on it — which rejected every
            // genuine cXML PO. Read with DtdProcessing.Ignore (skip the DOCTYPE; do NOT
            // fetch or expand it) and XmlResolver = null (no external entity resolution),
            // which keeps us safe from XXE while accepting the DOCTYPE these documents require.
            // Real-world cXML (Coupa/Ariba/Jaggaer) always carries a <!DOCTYPE>; load it
            // DOCTYPE-tolerantly but XXE-safe. See DtdSafeXmlLoader.
            doc = DtdSafeXmlLoader.Load(fileStream);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            throw new CxmlParseException($"cXML document could not be parsed: {ex.Message}", ex);
        }

        // Support both namespaced and bare root elements
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "cXML", StringComparison.OrdinalIgnoreCase))
            throw new CxmlParseException("Document root element is not <cXML>.");

        // ── Request (required) ─────────────────────────────────────────────
        var requestEl = GetDescendant(root, "Request")
            ?? throw new CxmlParseException("Required element <Request> is missing.");

        // deploymentMode is OPTIONAL in cXML and defaults to "production" — Coupa and
        // several other systems omit it on OrderRequest. Requiring it rejected valid
        // real-world POs, so treat an absent attribute as "production" rather than failing.
        var deploymentMode = requestEl.Attribute("deploymentMode")?.Value;
        if (string.IsNullOrWhiteSpace(deploymentMode))
            deploymentMode = "production";

        // ── OrderRequestHeader (required) ──────────────────────────────────
        var orderRequestEl = GetDescendant(root, "OrderRequest")
            ?? throw new CxmlParseException("Required element <OrderRequest> is missing.");

        var headerEl = GetDescendant(orderRequestEl, "OrderRequestHeader")
            ?? throw new CxmlParseException("Required element <OrderRequestHeader> is missing.");

        var orderId = headerEl.Attribute("orderID")?.Value;
        if (string.IsNullOrWhiteSpace(orderId))
            throw new CxmlParseException("Required attribute orderID is missing on <OrderRequestHeader>.");

        var orderDateStr = headerEl.Attribute("orderDate")?.Value;
        var (orderDate, orderDateAmbiguous) = ParseDate(orderDateStr);

        // ── Currency AND grand total from Total/Money ──────────────────────
        // The element carries both: the currency on @currency and the stated order total
        // as its text. Only the attribute used to be read, so every cXML order arrived with
        // GrandTotal = null and the emitters fell back to a derived line sum — which differs
        // from the stated total whenever the document carries shipping or header-level tax.
        var totalMoneyEl = GetDescendant(headerEl, "Total")
            .Let(t => GetDescendant(t, "Money"));
        var currency = totalMoneyEl?.Attribute("currency")?.Value;
        var (grandTotal, _) = ParseDecimal(totalMoneyEl?.Value);

        // Fallback: pick currency from first ItemOut/ItemDetail/UnitPrice/Money
        if (string.IsNullOrWhiteSpace(currency))
        {
            var firstMoney = GetDescendant(root, "UnitPrice").Let(u => GetDescendant(u, "Money"));
            currency = firstMoney?.Attribute("currency")?.Value;
        }

        // ── Buyer name from Header/From/Credential/Identity ───────────────
        var fromEl    = GetDescendant(root, "From");
        var buyerName = fromEl is not null
            ? GetDescendant(fromEl, "Identity")?.Value?.Trim()
            : null;

        // ── ShipTo / BillTo party blocks ───────────────────────────────────
        // Read from OrderRequestHeader's DIRECT children. Descendant search would be wrong
        // here: <Fax> carries the same <TelephoneNumber> shape as <Phone>, and the cXML
        // <Header><To><Correspondent><Contact> block is a routing contact, not the order's.
        var parties = new List<ParsedParty>(2);
        var shipTo = BuildParty(GetChild(headerEl, "ShipTo"), "shipTo");
        if (shipTo is not null) parties.Add(shipTo);
        var billTo = BuildParty(GetChild(headerEl, "BillTo"), "billTo");
        if (billTo is not null) parties.Add(billTo);

        // ── Order contact: OrderRequestHeader/Contact ──────────────────────
        // Scoped to a direct child for the same reason as the address blocks above.
        var contactEl    = GetChild(headerEl, "Contact");
        var contactName  = NullIfEmpty(GetChild(contactEl, "Name")?.Value);
        var contactEmail = NullIfEmpty(GetChild(contactEl, "Email")?.Value);
        var contactPhone = ComposePhone(GetChild(contactEl, "Phone"));

        // ── ItemOut elements (required: at least one) ──────────────────────
        var itemOuts = GetAllDescendants(root, "ItemOut").ToList();
        if (itemOuts.Count == 0)
            throw new CxmlParseException("At least one <ItemOut> element is required.");

        var lines = new List<ParsedOrderLine>(itemOuts.Count);
        int autoLine = 1;

        foreach (var itemOut in itemOuts)
        {
            var lineNumberAttr = itemOut.Attribute("lineNumber")?.Value;
            var lineNumber = int.TryParse(lineNumberAttr, out var ln) ? ln : autoLine;

            var quantityAttr = itemOut.Attribute("quantity")?.Value;
            var (quantityVal, qtyAmbiguous) = ParseDecimal(quantityAttr);
            var quantity = quantityVal ?? 0m;

            // Per-line requested delivery date. cXML carries it as ItemOut@requestedDeliveryDate
            // (an ISO 8601 dateTime, e.g. "2026-07-17T03:30:00-07:00"); the canonical model's
            // per-line slot is a DateOnly, so the offset-local calendar date is kept.
            var lineDeliveryDate = ParseCxmlDateOnly(itemOut.Attribute("requestedDeliveryDate")?.Value);

            // SupplierPartID (required per line)
            var itemIdEl      = GetDescendant(itemOut, "ItemID");
            var supplierPartId = GetDescendant(itemIdEl, "SupplierPartID")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(supplierPartId))
                throw new CxmlParseException(
                    $"Required element <SupplierPartID> is missing or empty on ItemOut lineNumber={lineNumberAttr ?? autoLine.ToString()}.");

            // ItemDetail
            var itemDetailEl = GetDescendant(itemOut, "ItemDetail");

            // UnitPrice/Money (required per line)
            var unitPriceMoneyEl = GetDescendant(itemDetailEl, "UnitPrice")
                .Let(u => GetDescendant(u, "Money"));
            if (unitPriceMoneyEl is null)
                throw new CxmlParseException(
                    $"Required element <UnitPrice><Money> is missing on ItemOut lineNumber={lineNumberAttr ?? autoLine.ToString()}.");

            var (unitPrice, priceAmbiguous) = ParseDecimal(unitPriceMoneyEl.Value);

            // Line currency overrides header if different
            var lineCurrency = unitPriceMoneyEl.Attribute("currency")?.Value;
            if (!string.IsNullOrWhiteSpace(lineCurrency) && string.IsNullOrWhiteSpace(currency))
                currency = lineCurrency;

            var description = GetDescendant(itemDetailEl, "Description")?.Value?.Trim();
            var unit        = GetDescendant(itemDetailEl, "UnitOfMeasure")?.Value?.Trim();

            // Lossless capture of the real identifiers cXML carries — these are the
            // genuine codes (e.g. ManufacturerPartID "TSP23S/A"), NOT something to be
            // guessed downstream. ManufacturerPartNumber is the catalog/enrichment key
            // and the high-confidence source for a supplier-code suggestion.
            var mpn = GetDescendant(itemDetailEl, "ManufacturerPartID")?.Value?.Trim();
            // The brand that goes WITH the part number. Punchout orders (Ariba/Coupa) routinely
            // carry it — "Litware" alongside "LTQ2500-BK-BTK1" — and without it the review UI
            // can only show a bare code the operator has to look up by hand.
            var manufacturerName = GetDescendant(itemDetailEl, "ManufacturerName")?.Value?.Trim();
            var auxId = GetDescendant(itemIdEl, "SupplierPartAuxiliaryID")?.Value?.Trim();
            var unspsc = GetDescendant(itemDetailEl, "Classification")?.Value?.Trim();
            if (string.Equals(unspsc, "unknown", StringComparison.OrdinalIgnoreCase))
                unspsc = null;   // cXML's literal placeholder, not a real classification

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: supplierPartId,   // cXML supplier-side: SupplierPartID used as the item code
                Description:   NullIfEmpty(description),
                Quantity:      quantity,
                Unit:          NullIfEmpty(unit),
                UnitPrice:     unitPrice,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the parser
                // could not read unambiguously flags the line for human review. Mirrors
                // CsvOrderParser/EdifactOrderParser's NeedsReview/ReviewReason contract.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous),
                DeliveryDate:           lineDeliveryDate,
                ManufacturerPartNumber: NullIfEmpty(mpn),
                CustomerPartNumber:     NullIfEmpty(auxId),
                Unspsc:                 NullIfEmpty(unspsc),
                ManufacturerName:       NullIfEmpty(manufacturerName)));

            autoLine++;
        }

        // Header-level requested delivery date. cXML states the date per ItemOut, not on the
        // header, so the canonical header field takes the EARLIEST line date — the date by
        // which the order as a whole must start arriving. Null when no line states one.
        var requestedDeliveryDate = lines
            .Select(l => l.DeliveryDate)
            .Where(d => d is not null)
            .Min();

        return new ParsedOrder(
            PoNumber:  NullIfEmpty(orderId),
            OrderDate: orderDate,
            BuyerName: NullIfEmpty(buyerName),
            Currency:  NullIfEmpty(currency?.ToUpperInvariant()),
            Lines:     lines,
            GrandTotal: grandTotal,
            RequestedDeliveryDate: requestedDeliveryDate,
            Parties:      parties.Count > 0 ? parties : null,
            ContactName:  contactName,
            ContactEmail: contactEmail,
            ContactPhone: contactPhone,
            // Only reachable via the non-conformant leniency path below — a conformant
            // ISO 8601 orderDate can never be ambiguous.
            NeedsReview:  orderDateAmbiguous,
            ReviewReason: DateParsing.BuildAmbiguityReason(orderDateAmbiguous, "order date", orderDateStr));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first descendant with the given local name, checking both the
    /// bare name and the cXML namespace. Returns null if not found.
    /// </summary>
    private static XElement? GetDescendant(XElement? parent, string localName)
    {
        if (parent is null) return null;
        return parent.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<XElement> GetAllDescendants(XElement parent, string localName) =>
        parent.Descendants().Where(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first DIRECT CHILD with the given local name, checking both the bare name
    /// and the cXML namespace. Distinct from <see cref="GetDescendant"/>: the address blocks
    /// repeat element names at several depths (<c>Address/Phone/TelephoneNumber</c> vs
    /// <c>Address/Fax/TelephoneNumber</c>, <c>OrderRequestHeader/Contact</c> vs
    /// <c>Header/To/Correspondent/Contact</c>), so a descendant search silently picks the
    /// wrong node.
    /// </summary>
    private static XElement? GetChild(XElement? parent, string localName)
    {
        if (parent is null) return null;
        return parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<XElement> GetChildren(XElement? parent, string localName) =>
        parent is null
            ? Enumerable.Empty<XElement>()
            : parent.Elements().Where(e =>
                string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a cXML <c>&lt;ShipTo&gt;</c> / <c>&lt;BillTo&gt;</c> container onto a
    /// <see cref="ParsedParty"/>. Shape:
    /// <code>
    /// &lt;ShipTo&gt;&lt;Address isoCountryCode="SE" addressID="SWZ"&gt;
    ///   &lt;Name xml:lang="en"&gt;…&lt;/Name&gt;
    ///   &lt;PostalAddress name="default"&gt;
    ///     &lt;DeliverTo&gt;…&lt;/DeliverTo&gt;&lt;Street&gt;…&lt;/Street&gt;&lt;City&gt;…&lt;/City&gt;
    ///     &lt;PostalCode&gt;…&lt;/PostalCode&gt;&lt;Country isoCountryCode="SE"&gt;Sweden&lt;/Country&gt;
    ///   &lt;/PostalAddress&gt;
    ///   &lt;Email name="default"&gt;…&lt;/Email&gt;&lt;Phone&gt;…&lt;/Phone&gt;
    /// &lt;/Address&gt;&lt;/ShipTo&gt;
    /// </code>
    /// Returns null when the container is absent or carries no name — the ingestion layer
    /// denormalises the party onto the flat ShipTo*/BillTo* columns keyed on the NAME, so a
    /// nameless party would produce an address block no emitter would ever write.
    /// </summary>
    private static ParsedParty? BuildParty(XElement? container, string role)
    {
        var addressEl = GetChild(container, "Address");
        if (addressEl is null) return null;

        var name = NullIfEmpty(GetChild(addressEl, "Name")?.Value);
        if (name is null) return null;

        var postalEl = GetChild(addressEl, "PostalAddress");

        // cXML permits repeated <Street> (and <DeliverTo>) lines for multi-line addresses.
        // Join the street lines rather than dropping all but the first; take the first
        // DeliverTo, which is the attention-of person (later ones repeat the company).
        var street = NullIfEmpty(string.Join(", ", GetChildren(postalEl, "Street")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)));

        var countryEl = GetChild(postalEl, "Country");
        // Prefer the ISO code over the display text: "Deutschland"/"Sweden" are localised
        // labels, and every downstream consumer of the country column wants the code.
        var country = NullIfEmpty(countryEl?.Attribute("isoCountryCode")?.Value)
                      ?? NullIfEmpty(addressEl.Attribute("isoCountryCode")?.Value)
                      ?? NullIfEmpty(countryEl?.Value);

        return new ParsedParty(
            Role:        role,
            Name:        name,
            Street:      street,
            City:        NullIfEmpty(GetChild(postalEl, "City")?.Value),
            PostalCode:  NullIfEmpty(GetChild(postalEl, "PostalCode")?.Value),
            Country:     country,
            EdiCode:     NullIfEmpty(addressEl.Attribute("addressID")?.Value),
            ContactName: NullIfEmpty(GetChildren(postalEl, "DeliverTo").FirstOrDefault()?.Value),
            Email:       NullIfEmpty(GetChild(addressEl, "Email")?.Value),
            Phone:       ComposePhone(GetChild(addressEl, "Phone")));
    }

    /// <summary>
    /// Flattens a cXML <c>&lt;Phone&gt;&lt;TelephoneNumber&gt;</c> composite
    /// (<c>CountryCode</c> + <c>AreaOrCityCode</c> + <c>Number</c>) into a single dialable
    /// string, e.g. <c>"+49 000 00000"</c>. Returns null when no number part is present.
    /// </summary>
    private static string? ComposePhone(XElement? phoneEl)
    {
        var telEl = GetChild(phoneEl, "TelephoneNumber");
        if (telEl is null) return NullIfEmpty(phoneEl?.Value);

        var countryCode = NullIfEmpty(GetChild(telEl, "CountryCode")?.Value);
        var areaCode    = NullIfEmpty(GetChild(telEl, "AreaOrCityCode")?.Value);
        var number      = NullIfEmpty(GetChild(telEl, "Number")?.Value);

        if (number is null && areaCode is null) return null;

        var parts = new List<string>(3);
        if (countryCode is not null) parts.Add("+" + countryCode);
        if (areaCode    is not null) parts.Add(areaCode);
        if (number      is not null) parts.Add(number);
        return NullIfEmpty(string.Join(" ", parts));
    }

    /// <summary>
    /// Reads a cXML ISO 8601 dateTime (<c>ItemOut@requestedDeliveryDate</c>) as a calendar
    /// date. The offset-local date is kept deliberately: "2026-07-17T03:30:00-07:00" is
    /// the 17th to the party that stated it, and converting to UTC would move it to the 18th.
    /// </summary>
    private static DateOnly? ParseCxmlDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var dto)
            ? DateOnly.FromDateTime(dto.DateTime)
            : null;
    }

    /// <summary>
    /// Parse a cXML date via the shared locale-aware reader. <c>orderDate</c> is ISO 8601 by
    /// the cXML 1.2 DTD, so a CONFORMANT document resolves unambiguously. The hand-rolled
    /// format array this replaced mishandled non-conformant senders in two ways: it omitted
    /// <c>d.M.yyyy</c>, so a German "12.06.2026" fell through to <c>DateTime.TryParse</c> and
    /// came back as <b>6 December</b> — a silent six-month error, and the OPPOSITE of what
    /// the same input yielded in CSV/XLSX/PDF; and it listed <c>dd/MM/yyyy</c> before
    /// <c>MM/dd/yyyy</c>, committing to day-first on any ≤12/≤12 date with nothing recording
    /// that a choice had been made. Returns <c>(value, ambiguous)</c>; the caller flags the
    /// order when ambiguous.
    /// </summary>
    private static (DateTime? Value, bool Ambiguous) ParseDate(string? value) =>
        DateParsing.TryParseHeaderDate(value);

    /// <summary>
    /// Parse a cXML numeric value via the shared locale-aware reader. cXML 1.2 is an
    /// invariant-locale standard: '.' is the decimal separator and ',' groups thousands,
    /// so <c>european: false</c>. Returns <c>(value, ambiguous)</c>; the caller flags the line
    /// for review when the token could not be read unambiguously rather than emitting a
    /// silently-wrong number. This replaces the old "swap ',' for '.' only when no '.' present"
    /// reader that read EU "1.234,56" as 1.23456 (group dropped) or null.
    /// </summary>
    private static (decimal? Value, bool Ambiguous) ParseDecimal(string? value) =>
        NumberParsing.TryParseFlexibleDecimal(value, european: false);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Extension helper for fluent null-safe chaining — avoids nested null checks
/// when traversing the cXML element tree.
/// </summary>
file static class XElementExtensions
{
    public static XElement? Let(this XElement? el, Func<XElement, XElement?> fn) =>
        el is null ? null : fn(el);
}
