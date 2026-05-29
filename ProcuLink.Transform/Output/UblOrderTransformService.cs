using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a UBL 2.1 Order-2 document (OASIS), compatible with the Peppol BIS
/// Order-only 3.0 profile, from a fully-resolved purchase order entity.
///
/// Output skeleton:
/// <code>
/// &lt;?xml version="1.0" encoding="UTF-8"?&gt;
/// &lt;Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
///         xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
///         xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"&gt;
///   &lt;cbc:UBLVersionID&gt;2.1&lt;/cbc:UBLVersionID&gt;
///   &lt;cbc:CustomizationID&gt;urn:fdc:peppol.eu:poacc:trns:order:3&lt;/cbc:CustomizationID&gt;
///   &lt;cbc:ProfileID&gt;urn:fdc:peppol.eu:poacc:bis:order_only:3&lt;/cbc:ProfileID&gt;
///   &lt;cbc:ID&gt;{poNumber}&lt;/cbc:ID&gt;
///   &lt;cbc:IssueDate&gt;{yyyy-MM-dd}&lt;/cbc:IssueDate&gt;
///   &lt;cbc:DocumentCurrencyCode&gt;{currency}&lt;/cbc:DocumentCurrencyCode&gt;
///   &lt;cac:BuyerCustomerParty&gt;&lt;cac:Party&gt;&lt;cac:PartyName&gt;&lt;cbc:Name&gt;{buyer}&lt;/cbc:Name&gt;&lt;/cac:PartyName&gt;&lt;/cac:Party&gt;&lt;/cac:BuyerCustomerParty&gt;
///   &lt;cac:SellerSupplierParty&gt;&lt;cac:Party&gt;&lt;cac:PartyName&gt;&lt;cbc:Name&gt;{supplier}&lt;/cbc:Name&gt;&lt;/cac:PartyName&gt;&lt;/cac:Party&gt;&lt;/cac:SellerSupplierParty&gt;
///   &lt;cac:OrderLine&gt;
///     &lt;cac:LineItem&gt;
///       &lt;cbc:ID&gt;{n}&lt;/cbc:ID&gt;
///       &lt;cbc:Quantity unitCode="EA"&gt;{qty}&lt;/cbc:Quantity&gt;
///       &lt;cbc:LineExtensionAmount currencyID="{currency}"&gt;{qty*price}&lt;/cbc:LineExtensionAmount&gt;
///       &lt;cac:Price&gt;&lt;cbc:PriceAmount currencyID="{currency}"&gt;{price}&lt;/cbc:PriceAmount&gt;&lt;/cac:Price&gt;
///       &lt;cac:Item&gt;
///         &lt;cbc:Name&gt;{description}&lt;/cbc:Name&gt;
///         &lt;cac:SellersItemIdentification&gt;&lt;cbc:ID&gt;{supplierItemCode}&lt;/cbc:ID&gt;&lt;/cac:SellersItemIdentification&gt;
///       &lt;/cac:Item&gt;
///     &lt;/cac:LineItem&gt;
///   &lt;/cac:OrderLine&gt;
/// &lt;/Order&gt;
/// </code>
///
/// Buyer and supplier party names are currently emitted as placeholders
/// ("ProcuLink Buyer" and the supplier id, respectively); a future pass will
/// pull real party metadata from buyer / supplier entities.
///
/// Validation mirrors <see cref="CxmlTransformService"/>: throws
/// <see cref="TransformValidationException"/> when any line still requires review
/// or is missing a SupplierItemCode.
/// </summary>
public sealed class UblOrderTransformService : ITransformService
{
    // ── UBL 2.1 namespaces ───────────────────────────────────────────────────
    private static readonly XNamespace UblOrder = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private static readonly XNamespace Cac      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // ── Peppol BIS 3.0 (order-only) identifiers ──────────────────────────────
    private const string PeppolBisCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";
    private const string PeppolBisProfileId       = "urn:fdc:peppol.eu:poacc:bis:order_only:3";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.Ubl;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct)
    {
        ValidateOrder(order);

        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency;

        // Placeholder party names — these will be wired to real buyer/supplier
        // metadata in a follow-up pass. Kept here so the document is valid UBL
        // (PartyName/Name is required by Peppol BIS 3.0 if PartyLegalEntity is absent).
        var buyerName    = "ProcuLink Buyer";
        var supplierName = order.SupplierId.ToString();

        var root = new XElement(UblOrder + "Order",
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName),

            new XElement(Cbc + "UBLVersionID",        "2.1"),
            new XElement(Cbc + "CustomizationID",     PeppolBisCustomizationId),
            new XElement(Cbc + "ProfileID",           PeppolBisProfileId),
            new XElement(Cbc + "ID",                  order.PoNumber),
            new XElement(Cbc + "IssueDate",           order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "DocumentCurrencyCode", currency),

            // ── BuyerCustomerParty (placeholder) ─────────────────────────────
            new XElement(Cac + "BuyerCustomerParty",
                new XElement(Cac + "Party",
                    new XElement(Cac + "PartyName",
                        new XElement(Cbc + "Name", buyerName)))),

            // ── SellerSupplierParty (placeholder) ────────────────────────────
            new XElement(Cac + "SellerSupplierParty",
                new XElement(Cac + "Party",
                    new XElement(Cac + "PartyName",
                        new XElement(Cbc + "Name", supplierName))))
        );

        // One OrderLine per purchase-order line, sorted deterministically.
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
            root.Add(BuildOrderLine(line, currency));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root);

        var bytes  = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "application/xml",
            FileExtension: ".xml"
        ));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static XElement BuildOrderLine(PurchaseOrderLineEntity line, string currency)
    {
        var quantityStr            = line.Quantity.ToString(CultureInfo.InvariantCulture);
        var unitPriceStr           = line.UnitPrice.ToString("F2", CultureInfo.InvariantCulture);
        var lineExtensionAmountStr = (line.Quantity * line.UnitPrice)
                                     .ToString("F2", CultureInfo.InvariantCulture);

        var unitCode = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit;

        return new XElement(Cac + "OrderLine",
            new XElement(Cac + "LineItem",
                new XElement(Cbc + "ID", line.LineNumber.ToString(CultureInfo.InvariantCulture)),

                new XElement(Cbc + "Quantity",
                    new XAttribute("unitCode", unitCode),
                    quantityStr),

                new XElement(Cbc + "LineExtensionAmount",
                    new XAttribute("currencyID", currency),
                    lineExtensionAmountStr),

                new XElement(Cac + "Price",
                    new XElement(Cbc + "PriceAmount",
                        new XAttribute("currencyID", currency),
                        unitPriceStr)),

                new XElement(Cac + "Item",
                    new XElement(Cbc + "Name", line.Description ?? string.Empty),
                    new XElement(Cac + "SellersItemIdentification",
                        new XElement(Cbc + "ID", line.SupplierItemCode ?? string.Empty)))));
    }

    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }
}
