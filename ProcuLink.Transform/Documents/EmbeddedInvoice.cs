using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Output;
using UglyToad.PdfPig;
using M = ProcuLink.Core.Documents.FieldMeanings;
using T = ProcuLink.Core.Documents.FieldTypes;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// Reads the invoice a ZUGFeRD 2.x / Factur-X / XRechnung PDF carries as an embedded UN/CEFACT CII file (EN 16931): the values
/// are the data itself, so they are taken at full confidence instead of being read from the page. MINIMUM and BASIC WL carry no lines,
/// so their lines table is empty. The attachment is untrusted: size-capped, no DTD, no resolver. Null when the PDF has no CII
/// invoice or it cannot be read: the page is then read as before. ZUGFeRD 1.0 (CrossIndustryDocument) is not read here.
/// </summary>
public static class EmbeddedInvoice
{
    public const int MaxXmlBytes = 5 * 1024 * 1024;
    /// <summary>The most rows a table takes; past it the read is marked truncated, as a page read past its limits is.</summary>
    public const int MaxRows = 1000;
    private static readonly string[] StandardNames = ["factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml"];

    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes };

    private const string Doc = "ExchangedDocument/", Tx = "SupplyChainTradeTransaction/";
    private const string Agreement = Tx + "ApplicableHeaderTradeAgreement/", Delivery = Tx + "ApplicableHeaderTradeDelivery/", Settlement = Tx + "ApplicableHeaderTradeSettlement/";
    private const string Seller = Agreement + "SellerTradeParty/", Buyer = Agreement + "BuyerTradeParty/", ShipTo = Delivery + "ShipToTradeParty/";
    private const string Terms = Settlement + "SpecifiedTradePaymentTerms/", Sums = Settlement + "SpecifiedTradeSettlementHeaderMonetarySummation/";
    private const string Address = "PostalTradeAddress/";

    private sealed record Spec(string Key, string Label, string Type, Func<XElement, string?> Value);

    private static Spec F(string key, string label, string type, string path) => new(key, label, type, e => Format(Text(e, path), type));

    // Labels are the EN 16931 business term names.
    private static readonly Spec[] Header =
    [
        F(M.DocumentNumber, "Invoice number", T.Text, Doc + "ID"),
        F(M.IssueDate, "Invoice issue date", T.Date, Doc + "IssueDateTime/DateTimeString"),
        F(M.DueDate, "Payment due date", T.Date, Terms + "DueDateDateTime/DateTimeString"),
        F(M.DeliveryDate, "Actual delivery date", T.Date, Delivery + "ActualDeliverySupplyChainEvent/OccurrenceDateTime/DateTimeString"),
        F(M.SenderName, "Seller name", T.Text, Seller + "Name"),
        new(M.SenderVatId, "Seller VAT identifier", T.VatId, e => Vat(e, Seller)),
        F(M.SenderStreet, "Seller address line 1", T.Text, Seller + Address + "LineOne"),
        F(M.SenderCity, "Seller city", T.Text, Seller + Address + "CityName"),
        F(M.SenderPostcode, "Seller post code", T.Text, Seller + Address + "PostcodeCode"),
        F(M.SenderCountry, "Seller country code", T.Code, Seller + Address + "CountryID"),
        F(M.SenderContactName, "Seller contact point", T.Text, Seller + "DefinedTradeContact/PersonName"),
        F(M.SenderContactPhone, "Seller contact telephone number", T.Text, Seller + "DefinedTradeContact/TelephoneUniversalCommunication/CompleteNumber"),
        F(M.SenderContactEmail, "Seller contact email address", T.Email, Seller + "DefinedTradeContact/EmailURIUniversalCommunication/URIID"),
        new(M.SenderEndpointId, "Seller electronic address", T.Code, e => Endpoint(e, Seller)),
        F(M.ReceiverName, "Buyer name", T.Text, Buyer + "Name"),
        new(M.ReceiverVatId, "Buyer VAT identifier", T.VatId, e => Vat(e, Buyer)),
        F(M.ReceiverStreet, "Buyer address line 1", T.Text, Buyer + Address + "LineOne"),
        F(M.ReceiverCity, "Buyer city", T.Text, Buyer + Address + "CityName"),
        F(M.ReceiverPostcode, "Buyer post code", T.Text, Buyer + Address + "PostcodeCode"),
        F(M.ReceiverCountry, "Buyer country code", T.Code, Buyer + Address + "CountryID"),
        new(M.ReceiverEndpointId, "Buyer electronic address", T.Code, e => Endpoint(e, Buyer)),
        F(M.DeliveryName, "Deliver to party name", T.Text, ShipTo + "Name"),
        F(M.DeliveryStreet, "Deliver to address line 1", T.Text, ShipTo + Address + "LineOne"),
        F(M.DeliveryCity, "Deliver to city", T.Text, ShipTo + Address + "CityName"),
        F(M.DeliveryPostcode, "Deliver to post code", T.Text, ShipTo + Address + "PostcodeCode"),
        F(M.DeliveryCountry, "Deliver to country code", T.Code, ShipTo + Address + "CountryID"),
        F(M.BuyerReference, "Buyer reference", T.Text, Agreement + "BuyerReference"),
        F(M.OrderReference, "Purchase order reference", T.Text, Agreement + "BuyerOrderReferencedDocument/IssuerAssignedID"),
        F(M.Currency, "Invoice currency code", T.Currency, Settlement + "InvoiceCurrencyCode"),
        F(M.Iban, "Payment account identifier", T.Iban, Settlement + "SpecifiedTradeSettlementPaymentMeans/PayeePartyCreditorFinancialAccount/IBANID"),
        F(M.PaymentReference, "Remittance information", T.Text, Settlement + "PaymentReference"),
        F(M.PaymentTerms, "Payment terms", T.Text, Terms + "Description"),
        F(M.TotalNet, "Invoice total amount without VAT", T.Money, Sums + "TaxBasisTotalAmount"),
        F(M.TotalTax, "Invoice total VAT amount", T.Money, Sums + "TaxTotalAmount"),
        F(M.TotalGross, "Invoice total amount with VAT", T.Money, Sums + "GrandTotalAmount"),
    ];

    private static readonly Spec[] LineColumns =
    [
        F(M.LineNumber, "Invoice line identifier", T.Text, "AssociatedDocumentLineDocument/LineID"),
        F(M.LineItemCode, "Item Seller's identifier", T.Code, "SpecifiedTradeProduct/SellerAssignedID"),
        F(M.LineBuyerItemCode, "Item Buyer's identifier", T.Code, "SpecifiedTradeProduct/BuyerAssignedID"),
        F(M.LineDescription, "Item name", T.Text, "SpecifiedTradeProduct/Name"),
        F(M.LineQuantity, "Invoiced quantity", T.Number, "SpecifiedLineTradeDelivery/BilledQuantity"),
        F(M.LineUnit, "Invoiced quantity unit of measure", T.Code, "SpecifiedLineTradeDelivery/BilledQuantity/@unitCode"),
        F(M.LineUnitPrice, "Item net price", T.Money, "SpecifiedLineTradeAgreement/NetPriceProductTradePrice/ChargeAmount"),
        F(M.LineTaxRate, "Invoiced item VAT rate", T.Number, "SpecifiedLineTradeSettlement/ApplicableTradeTax/RateApplicablePercent"),
        F(M.LineAmount, "Invoice line net amount", T.Money, "SpecifiedLineTradeSettlement/SpecifiedTradeSettlementLineMonetarySummation/LineTotalAmount"),
    ];

    private static readonly Spec[] VatColumns =
    [
        F("vat_category", "VAT category code", T.Code, "CategoryCode"),
        F("vat_rate", "VAT category rate", T.Number, "RateApplicablePercent"),
        F("taxable_amount", "VAT category taxable amount", T.Money, "BasisAmount"),
        F("vat_amount", "VAT category tax amount", T.Money, "CalculatedAmount"),
    ];

    /// <summary>The embedded invoice as a read, keyed as <paramref name="expected"/> lists it; null to read the page instead.</summary>
    public static ReadResult? Read(byte[] pdf, FlowFieldList? expected)
    {
        try { return Cii(pdf) is { } root ? Fit(Full(root), expected) : null; }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static XElement? Cii(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        if (!document.Advanced.TryGetEmbeddedFiles(out var files)) return null;
        static string Name(UglyToad.PdfPig.Content.EmbeddedFile f) => Path.GetFileName(string.IsNullOrEmpty(f.FileSpecification) ? f.Name ?? "" : f.FileSpecification);
        foreach (var file in files.Where(f => Name(f).EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => !StandardNames.Contains(Name(f), StringComparer.OrdinalIgnoreCase)))
        {
            if (file.Memory.Length > MaxXmlBytes) continue;
            using var reader = XmlReader.Create(new MemoryStream(file.Memory.ToArray()), Safe);
            if (XDocument.Load(reader).Root is { } root && root.Name == CiiInvoiceWriter.Rsm + "CrossIndustryInvoice") return root;
        }
        return null;
    }

    private static ReadResult Full(XElement root)
    {
        var truncated = false;
        ReadTable Table(string key, string label, Spec[] columns, IEnumerable<XElement> items)
        {
            var rows = items.Take(MaxRows + 1).ToList();
            truncated |= rows.Count > MaxRows;
            return new(key, label, columns.Select(c => new ReadColumn(c.Key, c.Label, c.Type, M.All.Contains(c.Key) ? c.Key : null)).ToList(),
                rows.Take(MaxRows).Select(i => new ReadRow(columns.ToDictionary(c => c.Key, c => new ReadCell(c.Value(i), 1.0, null, false, false)))).ToList(), Listed: true);
        }
        ReadTable[] tables =
        [
            Table("lines", "Invoice lines", LineColumns, All(root, Tx + "IncludedSupplyChainTradeLineItem")),
            Table("vat_breakdown", "VAT breakdown", VatColumns, All(root, Settlement + "ApplicableTradeTax")),
        ];
        return new ReadResult(Text(root, Doc + "TypeCode") == "381" ? "Credit note" : "Invoice",
            Header.Select(s => new ReadField(s.Key, s.Label, s.Value(root), s.Type, s.Key, 1.0, null, false, true, false)).Where(f => f.Value is not null).ToList(),
            tables, truncated);
    }

    /// <summary>
    /// Whether a value with this meaning is printed as the data writes it, so it must be found on the page. Decided by the meaning the
    /// value was read under (its spec above), never by a flow field's configurable type. Only dates (ISO here), the currency code,
    /// country codes and the line unit code need not be printed in that form; every other value, endpoint ids included, must be.
    /// An amount is compared as a number, but only when the page prints it as one word: "1 500,00" is not found, and the page is
    /// then read instead (the safe side).
    /// </summary>
    public static bool PrintedAsIs(string? meaning) => meaning is not (M.IssueDate or M.DueDate or M.DeliveryDate or M.Currency
        or M.SenderCountry or M.ReceiverCountry or M.DeliveryCountry or M.LineUnit);

    /// <summary>
    /// A listed field or column takes the value of its meaning, or else of the meaning its key or label names (FieldMeanings.Aliases).
    /// Every listed field comes back, empty when the invoice has no value for it, so a person fills a field the standard does not carry.
    /// </summary>
    private static ReadResult Fit(ReadResult full, FlowFieldList? expected)
    {
        if (expected is null) return full;
        static string? Bound(FlowField f, IEnumerable<string> keys) => f.Meaning is { } m ? keys.FirstOrDefault(k => k == m)
            : keys.FirstOrDefault(k => (string[])[DraftBinder.Plain(k), .. M.Aliases.GetValueOrDefault(k, [])] is var names
                && (names.Contains(DraftBinder.Plain(f.Key)) || names.Contains(DraftBinder.Plain(f.Label))));

        var fields = expected.Fields.Select(f => (f, Key: Bound(f, Header.Select(s => s.Key)))).Select(x => new ReadField(x.f.Key, x.f.Label,
            full.Fields.FirstOrDefault(y => y.Key == x.Key)?.Value, x.f.Type, x.f.Meaning ?? x.Key, 1.0, null, false, true, false)).ToList();
        var tables = expected.Tables.Select(t =>
        {
            var (source, keys) = full.Tables.Select(s => (s, Keys: t.Columns.ToDictionary(c => c.Key, c => Bound(c, s.Columns.Select(x => x.Key)))))
                .OrderByDescending(x => x.Keys.Values.Count(k => k is not null)).First();
            return new ReadTable(t.Key, t.Label, t.Columns.Select(c => new ReadColumn(c.Key, c.Label, c.Type, c.Meaning ?? keys[c.Key])).ToList(),
                keys.Values.Any(k => k is not null)
                    ? source.Rows.Select(r => new ReadRow(t.Columns.Where(c => keys[c.Key] is not null).ToDictionary(c => c.Key, c => r.Cells[keys[c.Key]!]))).ToList()
                    : [],
                Listed: true);
        }).ToList();
        return new ReadResult(full.KindLabel, fields, tables, full.Truncated);
    }

    private static IEnumerable<XElement> All(XElement e, string path) =>
        path.Split('/').Aggregate((IEnumerable<XElement>)[e], (xs, step) => xs.SelectMany(x => x.Elements().Where(c => c.Name.LocalName == step)));

    /// <summary>The first element's text, or its attribute after <c>/@</c>; null when empty.</summary>
    private static string? Text(XElement e, string path)
    {
        var at = path.IndexOf("/@", StringComparison.Ordinal);
        var x = All(e, at < 0 ? path : path[..at]).FirstOrDefault();
        var v = at < 0 ? x?.Value : x?.Attribute(path[(at + 2)..])?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string? Vat(XElement e, string party) =>
        All(e, party + "SpecifiedTaxRegistration/ID").FirstOrDefault(x => (string?)x.Attribute("schemeID") == "VA")?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>A Peppol participant id, scheme and id, only when the scheme is an ICD code (four digits).</summary>
    private static string? Endpoint(XElement e, string party) =>
        All(e, party + "URIUniversalCommunication/URIID").FirstOrDefault() is { } x && (string?)x.Attribute("schemeID") is { } scheme
        && Regex.IsMatch(scheme, @"^\d{4}$") && x.Value.Trim() is { Length: > 0 } id ? $"{scheme}:{id}" : null;

    /// <summary>A format-102 date as yyyy-MM-dd; an amount with at least two decimals, so "1.500" is never read as fifteen hundred;
    /// an IBAN in groups of four, as it is printed, so the page check can find it.</summary>
    private static string? Format(string? v, string type) => v is null ? null : type switch
    {
        T.Iban => Regex.Replace(v.Replace(" ", ""), ".{4}(?!$)", "$0 "),
        T.Date when DateOnly.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        T.Money or T.Number when decimal.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) => n.ToString("0.00##########", CultureInfo.InvariantCulture),
        _ => v,
    };
}
