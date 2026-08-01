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
/// The buyer party name is the canonical buyer name (<see cref="OrderHeaderReader.ExtractBuyerName"/>),
/// falling back to the legacy "ProcuLink Buyer" placeholder when the order carries none; the supplier
/// party name is still emitted as the supplier id (real supplier metadata is a future pass).
///
/// <para><b>Address + contact.</b> When the order carries address data the document additionally
/// emits a <c>cac:PostalAddress</c> + <c>cac:Contact</c> inside <c>BuyerCustomerParty/Party</c> (fed
/// from the canonical BillTo* + Contact* fields) and a <c>cac:Delivery</c> (fed from ShipTo*). Each
/// block is null-gated on its source NAME (Contact on any of its 3 fields), so an order with no
/// address data emits NONE of them and stays byte-identical to the pre-feature output. Country codes
/// are emitted verbatim (free-text — no ISO fabrication). The canonical model carries no buyer postal
/// address, so the buyer's real address rides on BillTo (and the delivery address on ShipTo).</para>
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
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: UBL has no cXML Header
    {
        // Existing review guard + format-required-field checks. UBL carries the line
        // code in an OPTIONAL identification element, so a missing code is not a hard
        // structural failure (the supplier-item-code / review guard still covers it);
        // a missing / zero unit price is flagged so a €0 document never delivers blind.
        OutputFieldValidator.ValidateEntity(order, format);

        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency;

        // Buyer name: the canonical buyer name, falling back to the legacy placeholder when blank so
        // the document stays valid UBL (PartyName/Name is required by Peppol BIS 3.0 if
        // PartyLegalEntity is absent). Supplier name remains the supplier id (placeholder).
        var resolvedBuyer = OrderHeaderReader.ExtractBuyerName(order);
        var buyerName     = string.IsNullOrWhiteSpace(resolvedBuyer) ? "ProcuLink Buyer" : resolvedBuyer;
        // Supplier name. This used to emit the supplier's GUID — so the party receiving the document
        // read its own name as `3f2b91c4-…`. The entity has carried a denormalized SupplierName since
        // the buyer-name column landed, and the loaded Supplier is the second source; the GUID stays
        // only as the last resort, because PartyName/Name may not be empty.
        //
        // Order matters: SupplierName is written by every current ingest path, and reading it avoids
        // depending on whether the Supplier navigation was included by the caller.
        var supplierName  = FirstNonBlank(
            order.SupplierName,
            order.SupplierId is null ? null : order.Supplier?.Name,
            (order.SupplierId ?? Guid.Empty).ToString());

        var root = new XElement(UblOrder + "Order",
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName),

            new XElement(Cbc + "UBLVersionID",        "2.1"),
            new XElement(Cbc + "CustomizationID",     PeppolBisCustomizationId),
            new XElement(Cbc + "ProfileID",           PeppolBisProfileId),
            new XElement(Cbc + "ID",                  order.PoNumber),
            new XElement(Cbc + "IssueDate",           order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "DocumentCurrencyCode", currency),

            // ── BuyerCustomerParty ────────────────────────────────────────────
            // PartyName + a null-gated PostalAddress (from BillTo*) + null-gated Contact (from
            // Contact*). Null helpers are dropped by the Party XElement → byte-identical when absent.
            new XElement(Cac + "BuyerCustomerParty",
                new XElement(Cac + "Party",
                    new XElement(Cac + "PartyName",
                        new XElement(Cbc + "Name", buyerName)),
                    BuildPostalAddress(order.BillToStreet, order.BillToCity,
                        order.BillToPostalCode, order.BillToCountry),
                    BuildContact(order))),

            // ── SellerSupplierParty (placeholder) ────────────────────────────
            new XElement(Cac + "SellerSupplierParty",
                new XElement(Cac + "Party",
                    new XElement(Cac + "PartyName",
                        new XElement(Cbc + "Name", supplierName))))
        );

        // ── Delivery (ship-to) — null-gated on ShipToName. Sits after the parties,
        // before the OrderLine loop (UBL 2.1 sequence: parties → Delivery → OrderLine). ──
        var delivery = BuildDelivery(order);
        if (delivery is not null)
            root.Add(delivery);

        // One OrderLine per purchase-order line, sorted deterministically.
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
            root.Add(BuildOrderLine(line, currency));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root);

        var bytes  = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(TransformResult.For(OutputFormat.Ubl, stream));
    }

    // ── Address / contact / delivery helpers ───────────────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Builds a <c>cac:PostalAddress</c> (StreetName / CityName / PostalZone / Country) with per-leaf
    /// null-drop, or null when ALL FOUR fields are blank (→ dropped by the parent → byte-identical for
    /// orders with no postal address). Country code is emitted verbatim (free text, no ISO fabrication).
    /// </summary>
    private static XElement? BuildPostalAddress(string? street, string? city, string? postal, string? country)
    {
        if (IsBlank(street) && IsBlank(city) && IsBlank(postal) && IsBlank(country))
            return null;

        return new XElement(Cac + "PostalAddress",
            IsBlank(street)  ? null : new XElement(Cbc + "StreetName", street),
            IsBlank(city)    ? null : new XElement(Cbc + "CityName",   city),
            IsBlank(postal)  ? null : new XElement(Cbc + "PostalZone", postal),
            IsBlank(country) ? null : new XElement(Cac + "Country",
                                          new XElement(Cbc + "IdentificationCode", country)));
    }

    /// <summary>
    /// Builds a <c>cac:Contact</c> (Name / Telephone / ElectronicMail) from the order's ordering
    /// contact, with per-leaf null-drop, or null when all three are blank (→ byte-identical when absent).
    /// </summary>
    private static XElement? BuildContact(PurchaseOrderEntity o)
    {
        if (IsBlank(o.ContactName) && IsBlank(o.ContactPhone) && IsBlank(o.ContactEmail))
            return null;

        return new XElement(Cac + "Contact",
            IsBlank(o.ContactName)  ? null : new XElement(Cbc + "Name",          o.ContactName),
            IsBlank(o.ContactPhone) ? null : new XElement(Cbc + "Telephone",     o.ContactPhone),
            IsBlank(o.ContactEmail) ? null : new XElement(Cbc + "ElectronicMail", o.ContactEmail));
    }

    /// <summary>
    /// Builds <c>cac:Delivery</c> from the ship-to address, or null when there is no ship-to name
    /// (→ no node → byte-identical for orders without a ship-to). Emits a
    /// <c>cac:DeliveryLocation/cac:Address</c> (from ShipTo street/city/postal/country) and a
    /// <c>cac:DeliveryParty/cac:PartyName/cbc:Name</c> (from ShipToName). Gated on the NAME because
    /// the DeliveryParty name is the only always-meaningful ship-to field.
    /// </summary>
    private static XElement? BuildDelivery(PurchaseOrderEntity o)
    {
        if (IsBlank(o.ShipToName))
            return null;

        var address = BuildPostalAddress(o.ShipToStreet, o.ShipToCity, o.ShipToPostalCode, o.ShipToCountry);

        return new XElement(Cac + "Delivery",
            address is null ? null
                : new XElement(Cac + "DeliveryLocation",
                    new XElement(Cac + "Address", address.Elements())),
            new XElement(Cac + "DeliveryParty",
                new XElement(Cac + "PartyName",
                    new XElement(Cbc + "Name", o.ShipToName))));
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

    /// <summary>
    /// The first candidate that is neither null nor whitespace. The last candidate is the fallback
    /// and is returned even if it is blank, so the caller decides what "nothing left" means.
    /// </summary>
    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }
        return candidates.Length == 0 ? string.Empty : candidates[^1] ?? string.Empty;
    }
}
