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
            doc = DtdSafeXmlLoader.Load(fileStream);   // DOCTYPE-tolerant, XXE-safe
            ct.ThrowIfCancellationRequested();
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
        var issueDate     = ParseDate(issueDateStr, "IssueDate");
        var dueDate       = ParseDateNullable(GetDescendantText(root, "PaymentDueDate")
                         ?? GetDescendantText(root, "DueDate"), "PaymentDueDate");
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
                          ?? GetText(legalMonetary, "LineExtensionAmount"), "TaxExclusiveAmount");
        var grandTotal     = ParseDecimal(GetText(legalMonetary, "PayableAmount")
                          ?? GetText(legalMonetary, "TaxInclusiveAmount"), "PayableAmount");
        var taxTotal       = ParseDecimal(GetDescendantText(root, "TaxAmount"), "TaxAmount");

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
        var quantity    = ParseDecimal(GetText(el, "InvoicedQuantity"), "InvoicedQuantity");
        var unitCode    = GetAttr(el, "InvoicedQuantity", "unitCode") ?? "EA";
        var unitPrice   = ParseDecimal(GetDescendantText(el, "PriceAmount"), "PriceAmount");
        var taxRate     = ParseDecimal(GetDescendantText(el, "Percent"), "Percent");
        var lineTotal   = ParseDecimal(GetText(el, "LineExtensionAmount"), "LineExtensionAmount");
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

    // ── Reading values: ABSENT and UNREADABLE are different facts ─────────
    //
    // These three helpers used to answer "I could not read this" with a value that looks
    // exactly like a real answer — today's date, or 0.00. That is the same class of defect
    // as a mis-read decimal separator: a wrong value returned where "unknown" was meant, with
    // nothing anywhere to say so. An invoice dated today because its date was corrupt is
    // indistinguishable from one genuinely issued today.
    //
    // The invoice path has no per-line review flag to raise (ParsedInvoiceLine has no
    // NeedsReview, and InvoiceLineEntity has no column for one), so the honest option here is
    // an explicit parse failure. ParseInvoiceJob already catches InvoiceParseException, marks
    // the invoice "failed", and surfaces it — so a document that cannot be read stops, loudly,
    // instead of being delivered wrong. The message carries the element AND the offending
    // token, because that message is what reaches the operator.
    //
    // An ABSENT or empty element keeps its existing, honest meaning: no <TaxTotal> really is
    // zero tax, and no <PaymentDueDate> really is "not stated". Only a value that is PRESENT
    // and unreadable is a failure.

    /// <summary>UBL amounts are xsd:decimal, so the document format itself declares the
    /// point-decimal convention. A token that decisively contradicts it — a real feed writing
    /// "1.234,56" or "73,22" — is still read from its own evidence by the shared reader.</summary>
    private const DecimalConvention UblDeclaredConvention = DecimalConvention.Point;

    private static DateOnly ParseDate(string? raw, string element)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvoiceParseException($"<{element}> is empty — an invoice must state its issue date.");

        return ParseDateOrNull(raw)
            ?? throw new InvoiceParseException(
                $"<{element}> could not be read as a date: '{raw.Trim()}'.");
    }

    private static DateOnly? ParseDateNullable(string? raw, string element)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return ParseDateOrNull(raw)
            ?? throw new InvoiceParseException(
                $"<{element}> could not be read as a date: '{raw.Trim()}'.");
    }

    /// <summary>
    /// The one shared reader, not a private copy.
    ///
    /// This method previously hand-rolled its own two-branch parse, and the lenient branch was
    /// <c>DateTime.TryParse(raw)</c> with NO <see cref="System.Globalization.CultureInfo"/> at
    /// all — so the same invoice meant different things on a German box and a US box, and
    /// <c>12.06.2026</c> was two different months depending on the server. Routing it through
    /// <see cref="DateParsing.TryParseFlexibleDate"/> makes it machine-independent and day-first
    /// by policy, and keeps every parser reading a date the same way.
    ///
    /// <c>cbc:IssueDate</c> is <c>xsd:date</c>, so the shared reader's ISO branch — which is
    /// tried first and is never ambiguous — handles every conformant document unchanged.
    ///
    /// Returning null on an unreadable value is what lets the two callers above keep the
    /// ABSENT-vs-UNREADABLE distinction: they turn that null into an explicit
    /// <c>InvoiceParseException</c> naming the element and the offending token, rather than
    /// today's date.
    /// </summary>
    private static DateOnly? ParseDateOrNull(string raw)
    {
        var (value, _) = DateParsing.TryParseFlexibleDate(raw, preferDayFirst: true);
        return value;
    }

    private static decimal ParseDecimal(string? raw, string element)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;                  // absent ≠ unreadable

        var (value, ambiguous) = NumberParsing.TryParseFlexibleDecimal(raw, UblDeclaredConvention);
        if (value is { } v && !ambiguous) return v;

        throw new InvoiceParseException(
            $"<{element}> could not be read as an amount: '{raw.Trim()}'.");
    }

    private static string? NullIfEmpty(string? v)
        => string.IsNullOrWhiteSpace(v) ? null : v;
}
