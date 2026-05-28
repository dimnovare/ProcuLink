using System.Text;
using System.Xml.Linq;
using System.Globalization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a UBL 2.1-compatible Invoice XML from an approved invoice entity.
/// </summary>
public sealed class XmlInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "xml";

    private static readonly XNamespace UblNs =
        "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CbcNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CacNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    public Task<byte[]> TransformAsync(
        InvoiceEntity invoice,
        IReadOnlyList<InvoiceLineEntity> lines,
        CancellationToken ct)
    {
        var cbc = CbcNs;
        var cac = CacNs;

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(UblNs + "Invoice",
                new XAttribute(XNamespace.Xmlns + "cbc", CbcNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "cac", CacNs.NamespaceName),
                new XElement(cbc + "ID", invoice.InvoiceNumber),
                new XElement(cbc + "IssueDate", invoice.IssueDate.ToString("yyyy-MM-dd")),
                invoice.DueDate.HasValue
                    ? new XElement(cbc + "DueDate", invoice.DueDate.Value.ToString("yyyy-MM-dd"))
                    : null,
                new XElement(cbc + "DocumentCurrencyCode", invoice.Currency),
                invoice.BuyerRef is not null
                    ? new XElement(cbc + "BuyerReference", invoice.BuyerRef)
                    : null,
                invoice.PaymentTerms is not null
                    ? new XElement(cbc + "Note", invoice.PaymentTerms)
                    : null,

                new XElement(cac + "TaxTotal",
                    new XElement(cbc + "TaxAmount",
                        new XAttribute("currencyID", invoice.Currency),
                        invoice.TaxTotal.ToString("F4", CultureInfo.InvariantCulture))),

                new XElement(cac + "LegalMonetaryTotal",
                    new XElement(cbc + "LineExtensionAmount",
                        new XAttribute("currencyID", invoice.Currency),
                        invoice.SubTotal.ToString("F4", CultureInfo.InvariantCulture)),
                    new XElement(cbc + "TaxExclusiveAmount",
                        new XAttribute("currencyID", invoice.Currency),
                        invoice.SubTotal.ToString("F4", CultureInfo.InvariantCulture)),
                    new XElement(cbc + "TaxInclusiveAmount",
                        new XAttribute("currencyID", invoice.Currency),
                        invoice.GrandTotal.ToString("F4", CultureInfo.InvariantCulture)),
                    new XElement(cbc + "PayableAmount",
                        new XAttribute("currencyID", invoice.Currency),
                        invoice.GrandTotal.ToString("F4", CultureInfo.InvariantCulture))),

                lines.OrderBy(l => l.LineNumber).Select(l =>
                    new XElement(cac + "InvoiceLine",
                        new XElement(cbc + "ID", l.LineNumber),
                        new XElement(cbc + "InvoicedQuantity",
                            new XAttribute("unitCode", l.UnitCode),
                            l.Quantity.ToString("F4", CultureInfo.InvariantCulture)),
                        new XElement(cbc + "LineExtensionAmount",
                            new XAttribute("currencyID", invoice.Currency),
                            l.LineTotal.ToString("F4", CultureInfo.InvariantCulture)),
                        l.BuyerItemCode is not null
                            ? new XElement(cac + "BuyersItemIdentification",
                                new XElement(cbc + "ID", l.BuyerItemCode))
                            : null,
                        l.SupplierItemCode is not null
                            ? new XElement(cac + "SellersItemIdentification",
                                new XElement(cbc + "ID", l.SupplierItemCode))
                            : null,
                        new XElement(cac + "Item",
                            new XElement(cbc + "Description", l.Description)),
                        new XElement(cac + "Price",
                            new XElement(cbc + "PriceAmount",
                                new XAttribute("currencyID", invoice.Currency),
                                l.UnitPrice.ToString("F4", CultureInfo.InvariantCulture)))))
            ));

        var bytes = Encoding.UTF8.GetBytes(doc.ToString());
        return Task.FromResult(bytes);
    }
}
