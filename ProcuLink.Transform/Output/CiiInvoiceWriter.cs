using System.Globalization;
using System.Xml.Linq;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Writes an EN 16931 UN/CEFACT Cross Industry Invoice (CII D16B) from the same typed invoice the Peppol UBL writer uses.
/// DECLARED BUT NOT VERIFIED: the guideline id <c>urn:cen.eu:en16931:2017</c> is the document's type, not a conformance claim.
/// The rule check is PeppolRulesProofTests (the official rules through the validator sidecar); that is a rule check, not network acceptance.
/// CII is an xs:sequence: element order below is the schema's. Electronic addresses are written only when given, and nothing is written empty.
/// Tax is collapsed to one category exactly as <see cref="PeppolBisInvoiceTransformService"/> does, using its rounding helpers.
/// </summary>
public static class CiiInvoiceWriter
{
    private static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    public static XDocument Build(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, PeppolPartyOptions parties, bool creditNote)
    {
        var currency = string.IsNullOrWhiteSpace(invoice.Currency) ? "EUR" : invoice.Currency;
        var category = invoice.TaxTotal > 0m ? "S" : "Z";
        var percent = PeppolBisInvoiceTransformService.DerivePercent(invoice, lines);

        XElement Tax(decimal rate, string cat) => new(Ram + "ApplicableTradeTax",
            new XElement(Ram + "TypeCode", "VAT"), new XElement(Ram + "CategoryCode", cat), new XElement(Ram + "RateApplicablePercent", Num(rate)));

        var root = new XElement(Rsm + "CrossIndustryInvoice",
            new XAttribute(XNamespace.Xmlns + "rsm", Rsm.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ram", Ram.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "udt", Udt.NamespaceName),
            new XElement(Rsm + "ExchangedDocumentContext",
                new XElement(Ram + "GuidelineSpecifiedDocumentContextParameter", new XElement(Ram + "ID", "urn:cen.eu:en16931:2017"))),
            new XElement(Rsm + "ExchangedDocument",
                new XElement(Ram + "ID", invoice.InvoiceNumber),
                new XElement(Ram + "TypeCode", creditNote ? "381" : "380"),
                new XElement(Ram + "IssueDateTime", Date(invoice.IssueDate))),
            new XElement(Rsm + "SupplyChainTradeTransaction",
                lines.OrderBy(l => l.LineNumber).Select(l => new XElement(Ram + "IncludedSupplyChainTradeLineItem",
                    new XElement(Ram + "AssociatedDocumentLineDocument", new XElement(Ram + "LineID", l.LineNumber)),
                    new XElement(Ram + "SpecifiedTradeProduct",
                        l.SupplierItemCode is null ? null : new XElement(Ram + "SellerAssignedID", l.SupplierItemCode),
                        l.BuyerItemCode is null ? null : new XElement(Ram + "BuyerAssignedID", l.BuyerItemCode),
                        new XElement(Ram + "Name", string.IsNullOrWhiteSpace(l.Description) ? "Item" : l.Description)),
                    new XElement(Ram + "SpecifiedLineTradeAgreement", new XElement(Ram + "NetPriceProductTradePrice", new XElement(Ram + "ChargeAmount", Num(l.UnitPrice)))),
                    new XElement(Ram + "SpecifiedLineTradeDelivery", new XElement(Ram + "BilledQuantity",
                        new XAttribute("unitCode", string.IsNullOrWhiteSpace(l.UnitCode) ? "EA" : l.UnitCode), l.Quantity.ToString("F4", CultureInfo.InvariantCulture))),
                    new XElement(Ram + "SpecifiedLineTradeSettlement",
                        Tax(PeppolBisInvoiceTransformService.NormalizePercent(l.TaxRate), l.TaxRate > 0m ? "S" : "Z"),
                        new XElement(Ram + "SpecifiedTradeSettlementLineMonetarySummation", new XElement(Ram + "LineTotalAmount", Num(l.LineTotal)))))),
                new XElement(Ram + "ApplicableHeaderTradeAgreement",
                    string.IsNullOrWhiteSpace(invoice.BuyerRef) ? null : new XElement(Ram + "BuyerReference", invoice.BuyerRef),
                    Party("SellerTradeParty", parties.SellerName, parties.SellerCountry, parties.SellerEndpointId, parties.SellerEndpointScheme, parties.SellerVatId),
                    Party("BuyerTradeParty", parties.BuyerName, parties.BuyerCountry, parties.BuyerEndpointId, parties.BuyerEndpointScheme, parties.BuyerVatId),
                    string.IsNullOrWhiteSpace(parties.OrderReference) ? null : new XElement(Ram + "BuyerOrderReferencedDocument", new XElement(Ram + "IssuerAssignedID", parties.OrderReference))),
                new XElement(Ram + "ApplicableHeaderTradeDelivery",
                    parties.DeliveryDate is { } d ? new XElement(Ram + "ActualDeliverySupplyChainEvent", new XElement(Ram + "OccurrenceDateTime", Date(d))) : null),
                new XElement(Ram + "ApplicableHeaderTradeSettlement",
                    new XElement(Ram + "InvoiceCurrencyCode", currency),
                    new XElement(Ram + "ApplicableTradeTax",
                        new XElement(Ram + "CalculatedAmount", Num(invoice.TaxTotal)), new XElement(Ram + "TypeCode", "VAT"),
                        new XElement(Ram + "BasisAmount", Num(invoice.SubTotal)), new XElement(Ram + "CategoryCode", category),
                        new XElement(Ram + "RateApplicablePercent", Num(percent))),
                    invoice.DueDate is { } due && !creditNote ? new XElement(Ram + "SpecifiedTradePaymentTerms", new XElement(Ram + "DueDateDateTime", Date(due))) : null,
                    new XElement(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation",
                        new XElement(Ram + "LineTotalAmount", Num(invoice.SubTotal)), new XElement(Ram + "TaxBasisTotalAmount", Num(invoice.SubTotal)),
                        new XElement(Ram + "TaxTotalAmount", new XAttribute("currencyID", currency), Num(invoice.TaxTotal)),
                        new XElement(Ram + "GrandTotalAmount", Num(invoice.GrandTotal)), new XElement(Ram + "DuePayableAmount", Num(invoice.GrandTotal))))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement Party(string wrapper, string? name, string? country, string? endpointId, string? scheme, string? vatId) => new(Ram + wrapper,
        string.IsNullOrWhiteSpace(name) ? null : new XElement(Ram + "Name", name),
        string.IsNullOrWhiteSpace(country) ? null : new XElement(Ram + "PostalTradeAddress", new XElement(Ram + "CountryID", country)),
        string.IsNullOrWhiteSpace(endpointId) ? null : new XElement(Ram + "URIUniversalCommunication",
            new XElement(Ram + "URIID", scheme is null ? null : new XAttribute("schemeID", scheme), endpointId)),
        string.IsNullOrWhiteSpace(vatId) ? null : new XElement(Ram + "SpecifiedTaxRegistration", new XElement(Ram + "ID", new XAttribute("schemeID", "VA"), vatId)));

    private static XElement Date(DateOnly d) => new(Udt + "DateTimeString", new XAttribute("format", "102"), d.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    private static string Num(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
