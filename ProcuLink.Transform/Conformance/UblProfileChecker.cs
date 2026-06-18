using System.Xml.Linq;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a UBL 2.1 Order-2 document (the output of <c>UblOrderTransformService</c>,
/// Peppol BIS Order-only 3.0 compatible) against
/// the named UBL 2.1 Order profile. Pragmatic structural + mandatory-element +
/// cardinality checks per OASIS UBL 2.1 Order and Peppol BIS 3.0 — not a full XSD
/// validator.
/// </summary>
internal sealed class UblProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.Ubl21Order;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "UBL 2.1 Order (Peppol BIS Order-only 3.0)", "2.1");

        XDocument? doc = null;
        var wellFormed = false;
        try
        {
            doc = XDocument.Parse(documentText);
            wellFormed = true;
        }
        catch (System.Xml.XmlException ex)
        {
            b.Add("ubl.wellformed", false, "XML 1.0",
                $"Document is not well-formed XML: {ex.Message}");
        }

        if (!wellFormed || doc?.Root is null)
        {
            b.Add("ubl.wellformed", wellFormed, "XML 1.0",
                wellFormed ? "Document is well-formed XML." : "Document has no root element.");
            FailRemaining(b);
            return b.Build();
        }

        b.Add("ubl.wellformed", true, "XML 1.0", "Document is well-formed XML.");
        var root = doc.Root;

        b.Require("ubl.root", string.Equals(root.Name.LocalName, "Order", StringComparison.Ordinal),
            "Order (root)", "Root element is <Order>.",
            $"Root element must be <Order> but was <{root.Name.LocalName}>.");

        // ── UBLVersionID must be 2.1 ───────────────────────────────────────────
        var version = ChildValue(root, "UBLVersionID");
        b.Require("ubl.version", string.Equals(version?.Trim(), "2.1", StringComparison.Ordinal),
            "cbc:UBLVersionID", "cbc:UBLVersionID is 2.1.",
            $"cbc:UBLVersionID must be 2.1 but was '{version ?? "(missing)"}'.");

        // ── CustomizationID + ProfileID (Peppol BIS identifiers) ───────────────
        b.Require("ubl.customizationID", HasNonEmpty(root, "CustomizationID"), "cbc:CustomizationID",
            "cbc:CustomizationID present.", "Mandatory cbc:CustomizationID is missing.");
        b.Require("ubl.profileID", HasNonEmpty(root, "ProfileID"), "cbc:ProfileID",
            "cbc:ProfileID present.", "Mandatory cbc:ProfileID is missing.");

        // ── ID (PO number) + IssueDate + DocumentCurrencyCode ──────────────────
        b.Require("ubl.id", HasNonEmpty(root, "ID"), "cbc:ID",
            "Mandatory cbc:ID present.", "Mandatory cbc:ID (order number) is missing or empty.");
        b.Require("ubl.issueDate", HasNonEmpty(root, "IssueDate"), "cbc:IssueDate",
            "Mandatory cbc:IssueDate present.", "Mandatory cbc:IssueDate is missing or empty.");
        b.Require("ubl.currency", HasNonEmpty(root, "DocumentCurrencyCode"), "cbc:DocumentCurrencyCode",
            "Mandatory cbc:DocumentCurrencyCode present.", "Mandatory cbc:DocumentCurrencyCode is missing or empty.");

        // ── BuyerCustomerParty/Party/PartyName/Name ────────────────────────────
        var buyerParty = Child(root, "BuyerCustomerParty");
        var buyerName  = buyerParty is null ? null
            : Child(buyerParty, "Party") is { } p
                && Child(p, "PartyName") is { } pn
                && Child(pn, "Name") is { } n && !string.IsNullOrWhiteSpace(n.Value)
                    ? n.Value : null;
        b.Require("ubl.buyerParty", buyerName is not null,
            "cac:BuyerCustomerParty/cac:Party/cac:PartyName/cbc:Name",
            "Buyer party name present.", "Mandatory BuyerCustomerParty Party/PartyName/Name is missing.");

        // ── OrderLine cardinality + LineItem mandatory children ────────────────
        var orderLines = Children(root, "OrderLine").ToList();
        b.Require("ubl.orderLine.cardinality", orderLines.Count >= 1, "cac:OrderLine (1..n)",
            $"{orderLines.Count} cac:OrderLine present.", "At least one cac:OrderLine is required.");

        if (orderLines.Count > 0)
        {
            var lineItems = orderLines.Select(ol => Child(ol, "LineItem")).ToList();
            var allHaveLineItem = lineItems.All(li => li is not null);
            var allHaveId       = lineItems.All(li => li is not null && HasNonEmpty(li, "ID"));
            var allHaveQty      = lineItems.All(li => li is not null && Child(li, "Quantity") is not null);
            var allHaveItem     = lineItems.All(li => li is not null && Child(li, "Item") is not null);

            b.Require("ubl.lineItem", allHaveLineItem, "cac:OrderLine/cac:LineItem",
                "Every OrderLine has a LineItem.", "An OrderLine is missing the mandatory cac:LineItem.");
            b.Require("ubl.lineItem.id", allHaveId, "cac:LineItem/cbc:ID",
                "Every LineItem has cbc:ID.", "A LineItem is missing the mandatory cbc:ID.");
            b.Require("ubl.lineItem.quantity", allHaveQty, "cac:LineItem/cbc:Quantity",
                "Every LineItem has cbc:Quantity.", "A LineItem is missing the mandatory cbc:Quantity.");
            b.Require("ubl.lineItem.item", allHaveItem, "cac:LineItem/cac:Item",
                "Every LineItem has cac:Item.", "A LineItem is missing the mandatory cac:Item.");
        }
        else
        {
            b.Add("ubl.lineItem", false, "cac:OrderLine/cac:LineItem", "No OrderLine to check.");
            b.Add("ubl.lineItem.id", false, "cac:LineItem/cbc:ID", "No OrderLine to check.");
            b.Add("ubl.lineItem.quantity", false, "cac:LineItem/cbc:Quantity", "No OrderLine to check.");
            b.Add("ubl.lineItem.item", false, "cac:LineItem/cac:Item", "No OrderLine to check.");
        }

        return b.Build();
    }

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        foreach (var (code, @ref) in new[]
                 {
                     ("ubl.root", "Order (root)"),
                     ("ubl.version", "cbc:UBLVersionID"),
                     ("ubl.customizationID", "cbc:CustomizationID"),
                     ("ubl.profileID", "cbc:ProfileID"),
                     ("ubl.id", "cbc:ID"),
                     ("ubl.issueDate", "cbc:IssueDate"),
                     ("ubl.currency", "cbc:DocumentCurrencyCode"),
                     ("ubl.buyerParty", "cac:BuyerCustomerParty/.../cbc:Name"),
                     ("ubl.orderLine.cardinality", "cac:OrderLine (1..n)"),
                     ("ubl.lineItem", "cac:OrderLine/cac:LineItem"),
                     ("ubl.lineItem.id", "cac:LineItem/cbc:ID"),
                     ("ubl.lineItem.quantity", "cac:LineItem/cbc:Quantity"),
                     ("ubl.lineItem.item", "cac:LineItem/cac:Item"),
                 })
            b.Add(code, false, @ref, "Not checked — document is not well-formed.");
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static string? ChildValue(XElement parent, string localName) =>
        Child(parent, localName)?.Value;

    private static bool HasNonEmpty(XElement parent, string localName) =>
        Child(parent, localName) is { } e && !string.IsNullOrWhiteSpace(e.Value);
}
