using System.Xml.Linq;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a cXML 1.2 <c>OrderRequest</c> document (the output of
/// <c>CxmlTransformService</c>) against the named cXML 1.2.024 OrderRequest
/// profile. Pragmatic structural + mandatory-element + cardinality checks per the
/// cXML DTD (http://xml.cxml.org/current/cXML.dtd) — not a full DTD validator.
/// </summary>
internal sealed class CxmlProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.CXml12OrderRequest;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "cXML 1.2 OrderRequest", "1.2.024");

        // ── Well-formedness ────────────────────────────────────────────────────
        XDocument? doc = null;
        var wellFormed = false;
        try
        {
            doc = XDocument.Parse(documentText);
            wellFormed = true;
        }
        catch (System.Xml.XmlException ex)
        {
            b.Add("cxml.wellformed", false, "XML 1.0",
                $"Document is not well-formed XML: {ex.Message}");
        }

        if (!wellFormed || doc?.Root is null)
        {
            // Cannot inspect further — record the dependent mandatory checks as failed.
            b.Add("cxml.wellformed", wellFormed, "XML 1.0",
                wellFormed ? "Document is well-formed XML." : "Document has no root element.");
            FailRemaining(b);
            return b.Build();
        }

        b.Add("cxml.wellformed", true, "XML 1.0", "Document is well-formed XML.");

        var root = doc.Root;

        // ── Root element <cXML> ────────────────────────────────────────────────
        var isCxml = string.Equals(root.Name.LocalName, "cXML", StringComparison.Ordinal);
        b.Require("cxml.root", isCxml, "cXML (root)",
            "Root element is <cXML>.",
            $"Root element must be <cXML> but was <{root.Name.LocalName}>.");

        // ── Mandatory envelope attributes: payloadID + timestamp ───────────────
        b.Require("cxml.payloadID", HasAttr(root, "payloadID"), "cXML/@payloadID",
            "Mandatory @payloadID present.", "Mandatory @payloadID is missing.");
        b.Require("cxml.timestamp", HasAttr(root, "timestamp"), "cXML/@timestamp",
            "Mandatory @timestamp present.", "Mandatory @timestamp is missing.");

        // ── Header / From / To credentials ─────────────────────────────────────
        var header = Child(root, "Header");
        b.Require("cxml.header", header is not null, "cXML/Header",
            "Mandatory <Header> present.", "Mandatory <Header> is missing.");

        var from = header is null ? null : Child(header, "From");
        var to   = header is null ? null : Child(header, "To");
        b.Require("cxml.header.from", CredentialIdentityPresent(from), "Header/From/Credential/Identity",
            "From Credential/Identity present.", "Header/From/Credential/Identity is missing.");
        b.Require("cxml.header.to", CredentialIdentityPresent(to), "Header/To/Credential/Identity",
            "To Credential/Identity present.", "Header/To/Credential/Identity is missing.");

        // ── Request / OrderRequest / OrderRequestHeader@orderID ────────────────
        var request      = Child(root, "Request");
        var orderRequest = request is null ? null : Child(request, "OrderRequest");
        b.Require("cxml.request.orderRequest", orderRequest is not null, "Request/OrderRequest",
            "Mandatory <OrderRequest> present.", "Mandatory Request/OrderRequest is missing.");

        var orderRequestHeader = orderRequest is null ? null : Child(orderRequest, "OrderRequestHeader");
        b.Require("cxml.orderRequestHeader", orderRequestHeader is not null, "OrderRequest/OrderRequestHeader",
            "Mandatory <OrderRequestHeader> present.", "Mandatory <OrderRequestHeader> is missing.");
        b.Require("cxml.orderRequestHeader.orderID",
            orderRequestHeader is not null && HasNonEmptyAttr(orderRequestHeader, "orderID"),
            "OrderRequestHeader/@orderID",
            "Mandatory @orderID present and non-empty.", "Mandatory OrderRequestHeader/@orderID is missing or empty.");
        b.Require("cxml.orderRequestHeader.total",
            orderRequestHeader is not null && Child(orderRequestHeader, "Total") is { } total && Child(total, "Money") is not null,
            "OrderRequestHeader/Total/Money",
            "Total/Money present.", "Mandatory OrderRequestHeader/Total/Money is missing.");

        // ── Lines: ≥1 ItemOut, each with @quantity + ItemID + UnitPrice/Money ──
        var items = orderRequest is null
            ? new List<XElement>()
            : Children(orderRequest, "ItemOut").ToList();

        b.Require("cxml.itemOut.cardinality", items.Count >= 1, "OrderRequest/ItemOut (1..n)",
            $"{items.Count} <ItemOut> line(s) present.", "At least one <ItemOut> line is required.");

        if (items.Count > 0)
        {
            var allHaveQty   = items.All(i => HasNonEmptyAttr(i, "quantity"));
            var allHaveItemId = items.All(i => Child(i, "ItemID") is not null);
            var allHaveMoney = items.All(i =>
                Child(i, "ItemDetail") is { } d && Child(d, "UnitPrice") is { } up && Child(up, "Money") is not null);

            b.Require("cxml.itemOut.quantity", allHaveQty, "ItemOut/@quantity",
                "Every ItemOut has @quantity.", "An ItemOut is missing the mandatory @quantity.");
            b.Require("cxml.itemOut.itemID", allHaveItemId, "ItemOut/ItemID",
                "Every ItemOut has <ItemID>.", "An ItemOut is missing the mandatory <ItemID>.");
            b.Require("cxml.itemOut.unitPrice", allHaveMoney, "ItemOut/ItemDetail/UnitPrice/Money",
                "Every ItemOut has UnitPrice/Money.", "An ItemOut is missing the mandatory UnitPrice/Money.");
        }
        else
        {
            b.Add("cxml.itemOut.quantity", false, "ItemOut/@quantity", "No ItemOut lines to check.");
            b.Add("cxml.itemOut.itemID", false, "ItemOut/ItemID", "No ItemOut lines to check.");
            b.Add("cxml.itemOut.unitPrice", false, "ItemOut/ItemDetail/UnitPrice/Money", "No ItemOut lines to check.");
        }

        return b.Build();
    }

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        b.Add("cxml.root", false, "cXML (root)", "Not checked — document is not well-formed.");
        b.Add("cxml.payloadID", false, "cXML/@payloadID", "Not checked — document is not well-formed.");
        b.Add("cxml.timestamp", false, "cXML/@timestamp", "Not checked — document is not well-formed.");
        b.Add("cxml.header", false, "cXML/Header", "Not checked — document is not well-formed.");
        b.Add("cxml.header.from", false, "Header/From/Credential/Identity", "Not checked — document is not well-formed.");
        b.Add("cxml.header.to", false, "Header/To/Credential/Identity", "Not checked — document is not well-formed.");
        b.Add("cxml.request.orderRequest", false, "Request/OrderRequest", "Not checked — document is not well-formed.");
        b.Add("cxml.orderRequestHeader", false, "OrderRequest/OrderRequestHeader", "Not checked — document is not well-formed.");
        b.Add("cxml.orderRequestHeader.orderID", false, "OrderRequestHeader/@orderID", "Not checked — document is not well-formed.");
        b.Add("cxml.orderRequestHeader.total", false, "OrderRequestHeader/Total/Money", "Not checked — document is not well-formed.");
        b.Add("cxml.itemOut.cardinality", false, "OrderRequest/ItemOut (1..n)", "Not checked — document is not well-formed.");
        b.Add("cxml.itemOut.quantity", false, "ItemOut/@quantity", "Not checked — document is not well-formed.");
        b.Add("cxml.itemOut.itemID", false, "ItemOut/ItemID", "Not checked — document is not well-formed.");
        b.Add("cxml.itemOut.unitPrice", false, "ItemOut/ItemDetail/UnitPrice/Money", "Not checked — document is not well-formed.");
    }

    // ── XML helpers (namespace-agnostic by local name) ─────────────────────────

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static bool HasAttr(XElement el, string name) =>
        el.Attributes().Any(a => string.Equals(a.Name.LocalName, name, StringComparison.Ordinal));

    private static bool HasNonEmptyAttr(XElement el, string name) =>
        el.Attributes().Any(a => string.Equals(a.Name.LocalName, name, StringComparison.Ordinal)
                              && !string.IsNullOrWhiteSpace(a.Value));

    private static bool CredentialIdentityPresent(XElement? party)
    {
        if (party is null) return false;
        var credential = Child(party, "Credential");
        if (credential is null) return false;
        var identity = Child(credential, "Identity");
        return identity is not null && !string.IsNullOrWhiteSpace(identity.Value);
    }
}
