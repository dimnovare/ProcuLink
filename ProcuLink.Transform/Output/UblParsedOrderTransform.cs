using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Serializes a canonical <see cref="ParsedOrder"/> into a UBL 2.1 Order-2
/// document (OASIS), compatible with the Peppol BIS Order-only 3.0 profile.
///
/// Round-trips with <see cref="UblOrderParser"/>: the bytes this emitter
/// produces parse back into an equivalent <see cref="ParsedOrder"/>. Because the
/// canonical model carries a single buyer-side item code (and no resolved
/// supplier code), the line item code is emitted as
/// <c>cac:BuyersItemIdentification/cbc:ID</c> — the field the parser reads first
/// for <see cref="ParsedOrderLine.BuyerItemCode"/>.
///
/// Output skeleton:
/// <code>
/// &lt;?xml version="1.0" encoding="UTF-8"?&gt;
/// &lt;Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
///         xmlns:cac="...CommonAggregateComponents-2"
///         xmlns:cbc="...CommonBasicComponents-2"&gt;
///   &lt;cbc:UBLVersionID&gt;2.1&lt;/cbc:UBLVersionID&gt;
///   &lt;cbc:CustomizationID&gt;urn:fdc:peppol.eu:poacc:trns:order:3&lt;/cbc:CustomizationID&gt;
///   &lt;cbc:ProfileID&gt;urn:fdc:peppol.eu:poacc:bis:order_only:3&lt;/cbc:ProfileID&gt;
///   &lt;cbc:ID&gt;{poNumber}&lt;/cbc:ID&gt;
///   &lt;cbc:IssueDate&gt;{yyyy-MM-dd}&lt;/cbc:IssueDate&gt;
///   &lt;cbc:DocumentCurrencyCode&gt;{currency}&lt;/cbc:DocumentCurrencyCode&gt;
///   &lt;cac:BuyerCustomerParty&gt;&lt;cac:Party&gt;&lt;cac:PartyName&gt;&lt;cbc:Name&gt;{buyer}&lt;/cbc:Name&gt;…
///   &lt;cac:OrderLine&gt;&lt;cac:LineItem&gt;
///     &lt;cbc:ID&gt;{n}&lt;/cbc:ID&gt;
///     &lt;cbc:Quantity unitCode="EA"&gt;{qty}&lt;/cbc:Quantity&gt;
///     &lt;cbc:LineExtensionAmount currencyID="{currency}"&gt;{qty*price}&lt;/cbc:LineExtensionAmount&gt;
///     &lt;cac:Price&gt;&lt;cbc:PriceAmount currencyID="{currency}"&gt;{price}&lt;/cbc:PriceAmount&gt;&lt;/cac:Price&gt;
///     &lt;cac:Item&gt;
///       &lt;cbc:Name&gt;{description}&lt;/cbc:Name&gt;
///       &lt;cac:BuyersItemIdentification&gt;&lt;cbc:ID&gt;{buyerItemCode}&lt;/cbc:ID&gt;…
///     &lt;/cac:Item&gt;
///   &lt;/cac:LineItem&gt;&lt;/cac:OrderLine&gt;
/// &lt;/Order&gt;
/// </code>
/// </summary>
public sealed class UblParsedOrderTransform : IParsedOrderTransform
{
    // ── UBL 2.1 namespaces ───────────────────────────────────────────────────
    private static readonly XNamespace UblOrder = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private static readonly XNamespace Cac      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // ── Peppol BIS 3.0 (order-only) identifiers ──────────────────────────────
    private const string PeppolBisCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";
    private const string PeppolBisProfileId       = "urn:fdc:peppol.eu:poacc:bis:order_only:3";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.UblOrder;

    public ParsedOrderTransformResult Transform(ParsedOrder order, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(order);

        // UBL carries the line code in an OPTIONAL identification element, so an empty
        // code is not a hard structural failure here — but a missing / zero unit price
        // still produces a financially-wrong document, so the price guard flags it for
        // review. No-op for a well-formed order — emitted bytes are unchanged.
        OutputFieldValidator.ValidateParsedOrder(order, format);

        var currency  = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency.Trim();
        var poNumber  = string.IsNullOrWhiteSpace(order.PoNumber) ? "UNKNOWN" : order.PoNumber.Trim();
        var issueDate = (order.OrderDate ?? DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var root = new XElement(UblOrder + "Order",
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName),

            new XElement(Cbc + "UBLVersionID",         "2.1"),
            new XElement(Cbc + "CustomizationID",      PeppolBisCustomizationId),
            new XElement(Cbc + "ProfileID",            PeppolBisProfileId),
            new XElement(Cbc + "ID",                   poNumber),
            new XElement(Cbc + "IssueDate",            issueDate),
            new XElement(Cbc + "DocumentCurrencyCode", currency),

            // ── BuyerCustomerParty ───────────────────────────────────────────
            new XElement(Cac + "BuyerCustomerParty",
                new XElement(Cac + "Party",
                    new XElement(Cac + "PartyName",
                        new XElement(Cbc + "Name",
                            string.IsNullOrWhiteSpace(order.BuyerName) ? "Buyer" : order.BuyerName.Trim()))))
        );

        // One OrderLine per canonical line, sorted deterministically.
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
            root.Add(BuildOrderLine(line, currency));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root);

        var text  = doc.Declaration + Environment.NewLine + doc.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);

        return new ParsedOrderTransformResult(
            Content:       bytes,
            ContentType:   "application/xml",
            FileExtension: ".xml");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static XElement BuildOrderLine(ParsedOrderLine line, string currency)
    {
        var unitPrice           = line.UnitPrice ?? 0m;
        var quantityStr         = line.Quantity.ToString(CultureInfo.InvariantCulture);
        var unitPriceStr        = unitPrice.ToString("F2", CultureInfo.InvariantCulture);
        var lineExtensionAmount = (line.Quantity * unitPrice).ToString("F2", CultureInfo.InvariantCulture);
        var unitCode            = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit.Trim();

        var item = new XElement(Cac + "Item",
            new XElement(Cbc + "Name", line.Description ?? string.Empty));

        // The canonical model carries one buyer-side code. Emit it as
        // BuyersItemIdentification so the parser recovers it as BuyerItemCode.
        if (!string.IsNullOrWhiteSpace(line.BuyerItemCode))
            item.Add(new XElement(Cac + "BuyersItemIdentification",
                new XElement(Cbc + "ID", line.BuyerItemCode.Trim())));

        return new XElement(Cac + "OrderLine",
            new XElement(Cac + "LineItem",
                new XElement(Cbc + "ID", line.LineNumber.ToString(CultureInfo.InvariantCulture)),

                new XElement(Cbc + "Quantity",
                    new XAttribute("unitCode", unitCode),
                    quantityStr),

                new XElement(Cbc + "LineExtensionAmount",
                    new XAttribute("currencyID", currency),
                    lineExtensionAmount),

                new XElement(Cac + "Price",
                    new XElement(Cbc + "PriceAmount",
                        new XAttribute("currencyID", currency),
                        unitPriceStr)),

                item));
    }
}
