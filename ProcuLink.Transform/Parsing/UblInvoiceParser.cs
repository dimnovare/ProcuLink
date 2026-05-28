using System.Xml.Linq;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses UBL 2.1 Invoice XML documents.
/// Namespace: urn:oasis:names:specification:ubl:schema:xsd:Invoice-2
/// Root element: Invoice
/// </summary>
public sealed class UblInvoiceParser : IInvoiceParser
{
    private const string UblInvoiceNs =
        "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";

    public bool CanParse(string fileExtension, string? contentType = null)
    {
        var ext = fileExtension.ToLowerInvariant();
        if (ext is ".xml" or ".ubl") return true;
        if (contentType is not null &&
            (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("ubl", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>
    /// Peek at the stream to determine if it is a UBL Invoice document.
    /// Does not consume the stream — resets position to 0 after checking.
    /// </summary>
    public static bool IsUblInvoiceDocument(Stream stream)
    {
        if (!stream.CanSeek) return false;
        var pos = stream.Position;
        try
        {
            var doc  = XDocument.Load(stream);
            var root = doc.Root;
            return root is not null
                && root.Name.LocalName == "Invoice"
                && root.Name.NamespaceName == UblInvoiceNs;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = pos;
        }
    }

    public async Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, ct);
        }
        catch (Exception ex)
        {
            throw new InvoiceParseException("Failed to load XML document.", ex);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Invoice")
            throw new InvoiceParseException(
                "Not a UBL Invoice document — root element must be <Invoice>.");

        // ── Header fields ─────────────────────────────────────────────────
        var invoiceNumber = GetText(root, "ID")
            ?? throw new InvoiceParseException("Missing <ID> (invoice number).");
        var issueDateStr  = GetText(root, "IssueDate")
            ?? throw new InvoiceParseException("Missing <IssueDate>.");
        var issueDate     = ParseDate(issueDateStr);
        var dueDate       = ParseDateNullable(GetDescendantText(root, "PaymentDueDate")
                         ?? GetDescendantText(root, "DueDate"));
        var currency      = root.Attribute("currencyID")?.Value
                         ?? GetText(root, "DocumentCurrencyCode")
                         ?? "EUR";

        var buyerRef      = GetDescendantText(root, "BuyerReference")
                         ?? GetDescendantText(root, "CustomerReference");
        var supplierRef   = GetDescendantText(root, "AccountingSupplierParty", "Party", "PartyIdentification", "ID");
        var paymentTerms  = GetDescendantText(root, "Note");

        // ── Monetary totals ───────────────────────────────────────────────
        var legalMonetary  = GetDescendant(root, "LegalMonetaryTotal");
        var subTotal       = ParseDecimal(GetText(legalMonetary, "TaxExclusiveAmount")
                          ?? GetText(legalMonetary, "LineExtensionAmount"));
        var grandTotal     = ParseDecimal(GetText(legalMonetary, "PayableAmount")
                          ?? GetText(legalMonetary, "TaxInclusiveAmount"));
        var taxTotal       = ParseDecimal(GetDescendantText(root, "TaxAmount"));

        // ── Lines ─────────────────────────────────────────────────────────
        var lineEls = GetAllDescendants(root, "InvoiceLine").ToList();
        if (lineEls.Count == 0)
            throw new InvoiceParseException("Invoice has no <InvoiceLine> elements.");

        var lines = lineEls.Select((el, i) => ParseLine(el, i + 1)).ToList();

        return new ParsedInvoice(
            InvoiceNumber: invoiceNumber,
            IssueDate:     issueDate,
            DueDate:       dueDate,
            Currency:      currency,
            BuyerRef:      NullIfEmpty(buyerRef),
            SupplierRef:   NullIfEmpty(supplierRef),
            PaymentTerms:  NullIfEmpty(paymentTerms),
            SubTotal:      subTotal,
            TaxTotal:      taxTotal,
            GrandTotal:    grandTotal,
            Lines:         lines);
    }

    private static ParsedInvoiceLine ParseLine(XElement el, int lineNum)
    {
        var id          = GetText(el, "ID") ?? lineNum.ToString();
        var description = GetDescendantText(el, "Description")
                       ?? GetDescendantText(el, "Name")
                       ?? string.Empty;
        var quantity    = ParseDecimal(GetText(el, "InvoicedQuantity"));
        var unitCode    = GetAttr(el, "InvoicedQuantity", "unitCode") ?? "EA";
        var unitPrice   = ParseDecimal(GetDescendantText(el, "PriceAmount"));
        var taxRate     = ParseDecimal(GetDescendantText(el, "Percent"));
        var lineTotal   = ParseDecimal(GetText(el, "LineExtensionAmount"));
        var buyerCode   = GetDescendantText(el, "BuyersItemIdentification", "ID");
        var supplierCode = GetDescendantText(el, "SellersItemIdentification", "ID");

        return new ParsedInvoiceLine(
            LineNumber:      int.TryParse(id, out var n) ? n : lineNum,
            Description:     description,
            Quantity:        quantity,
            UnitCode:        unitCode,
            UnitPrice:       unitPrice,
            TaxRate:         taxRate,
            LineTotal:       lineTotal,
            BuyerItemCode:   NullIfEmpty(buyerCode),
            SupplierItemCode: NullIfEmpty(supplierCode));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static XElement? GetDescendant(XElement? el, string localName)
        => el?.Descendants().FirstOrDefault(d => d.Name.LocalName == localName);

    private static IEnumerable<XElement> GetAllDescendants(XElement el, string localName)
        => el.Descendants().Where(d => d.Name.LocalName == localName);

    private static string? GetText(XElement? el, string childLocalName)
        => el?.Elements().FirstOrDefault(c => c.Name.LocalName == childLocalName)?.Value;

    private static string? GetDescendantText(XElement? el, params string[] chain)
    {
        if (el is null) return null;
        if (chain.Length == 1)
            return el.Descendants().FirstOrDefault(d => d.Name.LocalName == chain[0])?.Value;

        var current = el;
        foreach (var name in chain)
        {
            current = current?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
            if (current is null) return null;
        }
        return current?.Value;
    }

    private static string? GetAttr(XElement el, string childLocalName, string attrName)
        => el.Elements().FirstOrDefault(c => c.Name.LocalName == childLocalName)
             ?.Attribute(attrName)?.Value;

    private static DateOnly ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateOnly.FromDateTime(DateTime.UtcNow);
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(raw, out var dt))
            return DateOnly.FromDateTime(dt);
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static DateOnly? ParseDateNullable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(raw, out var dt))
            return DateOnly.FromDateTime(dt);
        return null;
    }

    private static decimal ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        return decimal.TryParse(raw,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    private static string? NullIfEmpty(string? v)
        => string.IsNullOrWhiteSpace(v) ? null : v;
}
