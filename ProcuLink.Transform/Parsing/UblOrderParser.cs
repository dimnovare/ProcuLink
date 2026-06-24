using System.Globalization;
using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses UBL 2.1 Order documents (OASIS) into a <see cref="ParsedOrder"/>.
/// Also handles Peppol BIS Order 3.x, which is a profile-restricted variant of UBL 2.1
/// identified by <c>&lt;CustomizationID&gt;urn:fdc:peppol.eu:poacc:trns:order:3&lt;/CustomizationID&gt;</c>.
///
/// Supported structure (cbc = CommonBasicComponents, cac = CommonAggregateComponents):
/// <code>
/// &lt;Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
///         xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
///         xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"&gt;
///   &lt;cbc:CustomizationID&gt;urn:fdc:peppol.eu:poacc:trns:order:3&lt;/cbc:CustomizationID&gt; &lt;!-- Peppol BIS 3 only --&gt;
///   &lt;cbc:ID&gt;PO-12345&lt;/cbc:ID&gt;
///   &lt;cbc:IssueDate&gt;2026-05-28&lt;/cbc:IssueDate&gt;
///   &lt;cbc:DocumentCurrencyCode&gt;EUR&lt;/cbc:DocumentCurrencyCode&gt;
///   &lt;cac:BuyerCustomerParty&gt;
///     &lt;cac:Party&gt;
///       &lt;cac:PartyName&gt;&lt;cbc:Name&gt;Acme Buyer Ltd&lt;/cbc:Name&gt;&lt;/cac:PartyName&gt;
///     &lt;/cac:Party&gt;
///   &lt;/cac:BuyerCustomerParty&gt;
///   &lt;cac:SellerSupplierParty&gt;
///     &lt;cac:Party&gt;
///       &lt;cac:PartyName&gt;&lt;cbc:Name&gt;Globex Supplier OY&lt;/cbc:Name&gt;&lt;/cac:PartyName&gt;
///     &lt;/cac:Party&gt;
///   &lt;/cac:SellerSupplierParty&gt;
///   &lt;cac:OrderLine&gt;
///     &lt;cac:LineItem&gt;
///       &lt;cbc:ID&gt;1&lt;/cbc:ID&gt;
///       &lt;cbc:Quantity unitCode="EA"&gt;10&lt;/cbc:Quantity&gt;
///       &lt;cac:Price&gt;
///         &lt;cbc:PriceAmount currencyID="EUR"&gt;125.00&lt;/cbc:PriceAmount&gt;
///       &lt;/cac:Price&gt;
///       &lt;cac:Item&gt;
///         &lt;cbc:Name&gt;Widget Type A&lt;/cbc:Name&gt;
///         &lt;cac:SellersItemIdentification&gt;&lt;cbc:ID&gt;SUP-ABC-001&lt;/cbc:ID&gt;&lt;/cac:SellersItemIdentification&gt;
///       &lt;/cac:Item&gt;
///     &lt;/cac:LineItem&gt;
///   &lt;/cac:OrderLine&gt;
/// &lt;/Order&gt;
/// </code>
///
/// Element resolution is namespace-aware but matches by local-name comparison
/// (same approach as <see cref="CxmlOrderParser"/>), so payloads with bare elements,
/// the UBL default namespace, or prefixed namespaces all parse uniformly.
///
/// FILE-DETECTION VS cXML — IMPORTANT:
/// Both <see cref="CxmlOrderParser"/> and <see cref="UblOrderParser"/> claim <c>.xml</c>.
/// True content-based mutual exclusivity requires the document root element + namespace
/// to be inspected. This parser exposes a static helper <see cref="IsUblOrderDocument"/>
/// that peeks the root element so that <see cref="OrderParserFactory"/> can disambiguate
/// when a content stream is available. With the current extension-only
/// <see cref="OrderParserFactory.GetParser"/>, registration order in <c>Program.cs</c>
/// is the disambiguator: place <see cref="UblOrderParser"/> AFTER <see cref="CxmlOrderParser"/>
/// only if cXML traffic dominates, or BEFORE if UBL/Peppol traffic dominates.
/// Recommended factory upgrade (documented in the agent-report deliverable): add a
/// stream-aware <c>GetParser(string ext, Stream peek)</c> overload that uses
/// <see cref="IsUblOrderDocument"/> and <see cref="CxmlOrderParser"/>-style root checks
/// to pick the correct XML parser before parsing.
///
/// Validation: throws <see cref="UblParseException"/> when required fields are absent
/// or when the document is not a UBL Order. Required per UBL 2.1: <c>cbc:ID</c> (header),
/// at least one <c>cac:OrderLine</c>/<c>cac:LineItem</c> with quantity, price, and an
/// item identifier (seller item id or item name).
/// </summary>
public sealed class UblOrderParser : IPurchaseOrderParser
{
    // ── UBL namespaces (used for the root-element peek; element traversal is local-name based) ──
    private const string UblOrderNs    = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private const string PeppolBisOrder3CustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";

    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".ubl", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileExtension, ".xml", StringComparison.OrdinalIgnoreCase);

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
            throw new UblParseException($"UBL document could not be parsed: {ex.Message}", ex);
        }

        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Order", StringComparison.OrdinalIgnoreCase))
            throw new UblParseException("Document root element is not <Order>.");

        // Namespace check — accept any document whose root local-name is Order AND whose
        // namespace is the UBL Order-2 namespace, OR whose root is bare <Order> (some
        // hand-edited test payloads omit the namespace; we keep the parser tolerant).
        var rootNs = root.Name.NamespaceName;
        if (!string.IsNullOrEmpty(rootNs) && !string.Equals(rootNs, UblOrderNs, StringComparison.Ordinal))
            throw new UblParseException(
                $"Document root <Order> has namespace '{rootNs}', expected '{UblOrderNs}' (UBL 2.1 Order).");

        // ── Header: ID (required) ──────────────────────────────────────────
        // In UBL the header ID is the first <cbc:ID> child of <Order> — not any descendant.
        // Avoid GetDescendant here because OrderLine/LineItem also has <cbc:ID>.
        var idEl = GetChild(root, "ID")
            ?? throw new UblParseException("Required element <cbc:ID> is missing on <Order>.");
        var orderId = idEl.Value?.Trim();
        if (string.IsNullOrWhiteSpace(orderId))
            throw new UblParseException("Required element <cbc:ID> on <Order> is empty.");

        // ── Header: IssueDate (recommended) ────────────────────────────────
        var issueDateStr = GetChild(root, "IssueDate")?.Value?.Trim();
        var orderDate    = ParseDate(issueDateStr);

        // ── Header: DocumentCurrencyCode (recommended) ─────────────────────
        var currency = GetChild(root, "DocumentCurrencyCode")?.Value?.Trim();

        // Fallback: pick currency from first LineItem/Price/PriceAmount@currencyID
        if (string.IsNullOrWhiteSpace(currency))
        {
            var firstPriceAmount = GetAllDescendants(root, "PriceAmount").FirstOrDefault();
            currency = firstPriceAmount?.Attribute("currencyID")?.Value
                       ?? firstPriceAmount?.Attribute(XName.Get("currencyID"))?.Value;
        }

        // ── Buyer party name (cac:BuyerCustomerParty/cac:Party/cac:PartyName/cbc:Name) ──
        var buyerName = ExtractPartyName(root, "BuyerCustomerParty");

        // ── Seller / supplier party name (cac:SellerSupplierParty/cac:Party/cac:PartyName/cbc:Name) ──
        // NOTE: SellerName is intentionally NOT propagated to ParsedOrder.BuyerName.
        // ParsedOrder.BuyerName carries the buyer; the supplier is identified separately
        // by SupplierId at the entity layer. SellerSupplierParty is parsed for future
        // use (e.g. supplier auto-resolution) but currently has no canonical-field home.
        _ = ExtractPartyName(root, "SellerSupplierParty");

        // ── Peppol BIS 3 customization (informational; not required) ───────
        // var customizationId = GetChild(root, "CustomizationID")?.Value?.Trim();
        // var isPeppolBis3    = string.Equals(customizationId, PeppolBisOrder3CustomizationId, StringComparison.Ordinal);
        // No behavioural branch on this flag today — Peppol BIS 3 is structurally
        // compatible with UBL 2.1 Order for the fields we read. Kept here as a hook
        // for future Peppol-only validations (e.g. mandatory EndpointID).

        // ── OrderLine elements (required: at least one) ────────────────────
        var orderLines = GetAllDescendants(root, "OrderLine").ToList();
        if (orderLines.Count == 0)
            throw new UblParseException("At least one <cac:OrderLine> element is required.");

        var lines = new List<ParsedOrderLine>(orderLines.Count);
        int autoLine = 1;

        foreach (var orderLineEl in orderLines)
        {
            var lineItemEl = GetDescendant(orderLineEl, "LineItem")
                ?? throw new UblParseException(
                    $"Required element <cac:LineItem> is missing on OrderLine #{autoLine}.");

            // Line number: cbc:ID is a direct child of LineItem
            var lineIdStr  = GetChild(lineItemEl, "ID")?.Value?.Trim();
            var lineNumber = int.TryParse(lineIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln)
                ? ln
                : autoLine;

            // Quantity: <cbc:Quantity unitCode="EA">10</cbc:Quantity>
            var quantityEl = GetChild(lineItemEl, "Quantity");
            var (quantityVal, qtyAmbiguous) = ParseDecimal(quantityEl?.Value);
            var quantity   = quantityVal ?? 0m;
            var unit       = quantityEl?.Attribute("unitCode")?.Value?.Trim();

            // Price: <cac:Price><cbc:PriceAmount currencyID="EUR">125.00</cbc:PriceAmount></cac:Price>
            var priceEl        = GetChild(lineItemEl, "Price");
            var priceAmountEl  = priceEl is null ? null : GetChild(priceEl, "PriceAmount");
            var (unitPrice, priceAmbiguous) = ParseDecimal(priceAmountEl?.Value);

            // Line currency overrides header if header lacked one
            var lineCurrency = priceAmountEl?.Attribute("currencyID")?.Value;
            if (!string.IsNullOrWhiteSpace(lineCurrency) && string.IsNullOrWhiteSpace(currency))
                currency = lineCurrency;

            // Item: cac:Item — name and seller's item identification
            var itemEl = GetChild(lineItemEl, "Item")
                ?? throw new UblParseException(
                    $"Required element <cac:Item> is missing on LineItem #{lineNumber}.");

            var itemName = GetChild(itemEl, "Name")?.Value?.Trim();

            var sellersItemIdEl = GetChild(itemEl, "SellersItemIdentification");
            var sellersItemId   = sellersItemIdEl is null
                ? null
                : GetChild(sellersItemIdEl, "ID")?.Value?.Trim();

            // Buyer's item id is preferred when present; fall back to seller's, then to name.
            var buyersItemIdEl = GetChild(itemEl, "BuyersItemIdentification");
            var buyersItemId   = buyersItemIdEl is null
                ? null
                : GetChild(buyersItemIdEl, "ID")?.Value?.Trim();

            // BuyerItemCode resolution priority:
            //   1. BuyersItemIdentification/ID  — most accurate when present
            //   2. SellersItemIdentification/ID — Peppol BIS Order 3 mandatory field; common
            //   3. Item/Name                    — last-resort textual identifier
            // The mapping layer downstream resolves SupplierItemCode from item_mappings,
            // so we never trust supplier-side identifiers blindly for that field.
            var buyerItemCode = NullIfEmpty(buyersItemId)
                              ?? NullIfEmpty(sellersItemId)
                              ?? NullIfEmpty(itemName);

            if (string.IsNullOrWhiteSpace(buyerItemCode))
                throw new UblParseException(
                    $"LineItem #{lineNumber} has no usable item identifier "
                  + "(<cac:BuyersItemIdentification><cbc:ID>, <cac:SellersItemIdentification><cbc:ID>, or <cbc:Name> required).");

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerItemCode!,
                Description:   NullIfEmpty(itemName),
                Quantity:      quantity,
                Unit:          NullIfEmpty(unit),
                UnitPrice:     unitPrice,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the parser
                // could not read unambiguously flags the line for human review. Mirrors
                // CsvOrderParser/EdifactOrderParser's NeedsReview/ReviewReason contract.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous)));

            autoLine++;
        }

        return new ParsedOrder(
            PoNumber:  NullIfEmpty(orderId),
            OrderDate: orderDate,
            BuyerName: NullIfEmpty(buyerName),
            Currency:  NullIfEmpty(currency?.ToUpperInvariant()),
            Lines:     lines);
    }

    // ── Public static helpers (factory-friendly) ─────────────────────────────

    /// <summary>
    /// Peeks an XML stream to determine whether it is a UBL 2.1 / Peppol BIS Order document.
    /// Returns true when the root element is <c>&lt;Order&gt;</c> AND the namespace is
    /// <see cref="UblOrderNs"/>. Used by an upgraded <see cref="OrderParserFactory"/> to
    /// disambiguate cXML vs UBL for <c>.xml</c> uploads. Does not consume the stream
    /// (resets position to the original value when supported).
    /// </summary>
    /// <remarks>
    /// Reads only the root element; safe to call on streaming uploads up to the first
    /// few hundred bytes. Returns <c>false</c> on any parse error rather than throwing —
    /// this is a probe, not a validation pass.
    /// </remarks>
    public static bool IsUblOrderDocument(Stream stream)
    {
        if (stream is null) return false;

        var originalPosition = stream.CanSeek ? stream.Position : -1L;
        try
        {
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                CloseInput   = false,
                IgnoreWhitespace = true,
                IgnoreComments   = true,
                DtdProcessing    = System.Xml.DtdProcessing.Prohibit
            });

            // Advance to the first element node — the root.
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                return string.Equals(reader.LocalName, "Order", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reader.NamespaceURI, UblOrderNs, StringComparison.Ordinal);
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

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Walks two levels: BuyerCustomerParty/Party/PartyName/Name (or SellerSupplierParty/...).
    /// Returns the textual party name, or null if any link in the chain is missing.
    /// Falls back to PartyLegalEntity/RegistrationName which Peppol BIS 3 commonly uses
    /// when PartyName is omitted.
    /// </summary>
    private static string? ExtractPartyName(XElement root, string partyContainerLocalName)
    {
        var containerEl = GetDescendant(root, partyContainerLocalName);
        if (containerEl is null) return null;

        var partyEl = GetChild(containerEl, "Party");
        if (partyEl is null) return null;

        // Primary: PartyName/Name
        var partyNameEl = GetChild(partyEl, "PartyName");
        var name = partyNameEl is null ? null : GetChild(partyNameEl, "Name")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;

        // Fallback: PartyLegalEntity/RegistrationName (Peppol BIS 3)
        var legalEl = GetChild(partyEl, "PartyLegalEntity");
        return legalEl is null ? null : GetChild(legalEl, "RegistrationName")?.Value?.Trim();
    }

    /// <summary>
    /// Returns the first <em>direct child</em> with the given local name, ignoring namespace.
    /// Use this when an element appears at multiple depths under different parents and you
    /// only want the one immediately under <paramref name="parent"/>.
    /// </summary>
    private static XElement? GetChild(XElement? parent, string localName)
    {
        if (parent is null) return null;
        return parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the first descendant with the given local name, ignoring namespace.
    /// Used for unique descendants where depth varies (e.g. <c>LineItem</c> under <c>OrderLine</c>).
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

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    /// <summary>
    /// Parse a UBL numeric value via the shared locale-aware reader. UBL 2.1 / Peppol BIS
    /// is an invariant-locale standard: '.' is the decimal separator and ',' groups thousands,
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
