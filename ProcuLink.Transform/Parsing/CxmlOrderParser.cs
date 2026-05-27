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
            doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, ct);
        }
        catch (Exception ex)
        {
            throw new CxmlParseException($"cXML document could not be parsed: {ex.Message}", ex);
        }

        // Support both namespaced and bare root elements
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "cXML", StringComparison.OrdinalIgnoreCase))
            throw new CxmlParseException("Document root element is not <cXML>.");

        // ── deploymentMode (required) ──────────────────────────────────────
        var requestEl = GetDescendant(root, "Request")
            ?? throw new CxmlParseException("Required element <Request> is missing.");

        var deploymentMode = requestEl.Attribute("deploymentMode")?.Value;
        if (string.IsNullOrWhiteSpace(deploymentMode))
            throw new CxmlParseException("Required attribute deploymentMode is missing on <Request>.");

        // ── OrderRequestHeader (required) ──────────────────────────────────
        var orderRequestEl = GetDescendant(root, "OrderRequest")
            ?? throw new CxmlParseException("Required element <OrderRequest> is missing.");

        var headerEl = GetDescendant(orderRequestEl, "OrderRequestHeader")
            ?? throw new CxmlParseException("Required element <OrderRequestHeader> is missing.");

        var orderId = headerEl.Attribute("orderID")?.Value;
        if (string.IsNullOrWhiteSpace(orderId))
            throw new CxmlParseException("Required attribute orderID is missing on <OrderRequestHeader>.");

        var orderDateStr = headerEl.Attribute("orderDate")?.Value;
        var orderDate    = ParseDate(orderDateStr);

        // ── Currency from Total/Money ──────────────────────────────────────
        var totalMoneyEl = GetDescendant(headerEl, "Total")
            .Let(t => GetDescendant(t, "Money"));
        var currency = totalMoneyEl?.Attribute("currency")?.Value;

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
            var quantity = ParseDecimal(quantityAttr) ?? 0m;

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

            var unitPrice = ParseDecimal(unitPriceMoneyEl.Value);

            // Line currency overrides header if different
            var lineCurrency = unitPriceMoneyEl.Attribute("currency")?.Value;
            if (!string.IsNullOrWhiteSpace(lineCurrency) && string.IsNullOrWhiteSpace(currency))
                currency = lineCurrency;

            var description = GetDescendant(itemDetailEl, "Description")?.Value?.Trim();
            var unit        = GetDescendant(itemDetailEl, "UnitOfMeasure")?.Value?.Trim();

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: supplierPartId,   // cXML supplier-side: SupplierPartID used as the item code
                Description:   NullIfEmpty(description),
                Quantity:      quantity,
                Unit:          NullIfEmpty(unit),
                UnitPrice:     unitPrice));

            autoLine++;
        }

        return new ParsedOrder(
            PoNumber:  NullIfEmpty(orderId),
            OrderDate: orderDate,
            BuyerName: NullIfEmpty(buyerName),
            Currency:  NullIfEmpty(currency?.ToUpperInvariant()),
            Lines:     lines);
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
