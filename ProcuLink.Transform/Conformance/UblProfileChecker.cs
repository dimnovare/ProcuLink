using System.Xml.Linq;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Checks a UBL 2.1 Order-2 document (the output of <c>UblOrderTransformService</c>) against the
/// vendored OASIS UBL 2.1 schema, plus a set of named presence and cardinality checks.
///
/// <para><b>What this is, stated plainly.</b> Two different kinds of check, and the difference
/// matters. The named checks (<c>ubl.root</c> … <c>ubl.lineItem.item</c>) are presence and
/// structure only — is the root <c>&lt;Order&gt;</c>, is <c>cbc:UBLVersionID</c> 2.1, are the
/// <c>minOccurs="1"</c> elements there and non-empty, is there at least one OrderLine with a
/// LineItem carrying ID / Quantity / Item. They are ProcuLink's reading of the standard, and they
/// are kept because they name the failure in procurement terms a schema error cannot
/// ("Mandatory cbc:ID (order number) is missing or empty" reads better than a grammar
/// message).</para>
///
/// <para><b><c>ubl.xsd</c> is the one that is not our opinion.</b> It validates the document
/// against the OASIS UBL 2.1 Order-2 schema itself — the vendored, unmodified, machine-readable
/// one (<c>Conformance/Schemas/ubl-2.1/</c>, provenance in <c>PROVENANCE.md</c>) — via
/// <see cref="UblSchemaValidator"/>. Everything else in
/// <c>ProcuLink.Transform/Conformance/</c> validates ProcuLink's output against ProcuLink's own
/// summary of a specification, which cannot be evidence about that specification. This check is
/// the exception, and it sees things a presence checker structurally cannot: UBL's content models
/// are ordered <c>xsd:sequence</c>es, so a document with every mandatory element present but two
/// of them transposed passes every named check below and fails <c>ubl.xsd</c>.</para>
///
/// <para>It is still NOT a business-rule engine and NOT a conformance certification against any
/// profile. See <see cref="UblSchemaValidator"/> for the precise claim.</para>
///
/// <para><b>It says nothing about Peppol, and used to.</b> The profile was named "UBL 2.1 Order
/// (Peppol BIS Order-only 3.0)" and two of its checks required <c>cbc:CustomizationID</c> and
/// <c>cbc:ProfileID</c> to be non-empty. That was circular: the emitter had just written both,
/// so on our own output the checks could not fail, and an always-passing result was rendered as
/// "Matches the standard" with the Peppol name beside it and a Download button under it. Both
/// checks are gone — UBL 2.1 declares those elements <c>minOccurs="0"</c>, so requiring them was
/// never a UBL rule in the first place — and the emitter no longer writes them. ProcuLink does not
/// verify Peppol BIS business rules anywhere; there is no Schematron in this repo.</para>
/// </summary>
internal sealed class UblProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.Ubl21Order;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "OASIS UBL 2.1 Order — schema validation and mandatory elements", "2.1");

        // The parse failure is carried out of the catch as a message rather than written as a
        // check there, so that "ubl.wellformed" is added on exactly one of the three paths below.
        // Code is a stable machine key (see ConformanceModels.cs): it crosses the wire as
        // ConformanceCheckDto, callers select on it with Single(), and ToMarkdown() renders one
        // row per check — so emitting it twice duplicates a row of the downloadable evidence
        // report. Pinned by ConformanceCheckCodeUniquenessTests.
        XDocument? doc = null;
        string? parseError = null;
        try
        {
            doc = XDocument.Parse(documentText);
        }
        catch (System.Xml.XmlException ex)
        {
            parseError = ex.Message;
        }

        if (doc?.Root is null)
        {
            b.Add("ubl.wellformed", false, "XML 1.0",
                parseError is not null
                    ? $"Document is not well-formed XML: {parseError}"
                    : "Document has no root element.");
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

        // cbc:CustomizationID and cbc:ProfileID are NOT checked. They are minOccurs="0" in the
        // OASIS UBL 2.1 Order-2 schema, so their absence is not a defect; and the checks that used
        // to be here asserted only that they were non-empty, on a document this codebase had just
        // written them into. See the class summary.

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

        AddSchemaCheck(b, documentText);

        return b.Build();
    }

    /// <summary>
    /// Adds <c>ubl.xsd</c> — validation against the vendored OASIS schema.
    ///
    /// <para><b>One check, not one per violation, deliberately.</b> A single misplaced element in an
    /// ordered UBL sequence cascades into a violation for every following sibling, so a
    /// row-per-violation report would be mostly echoes of one fault. More importantly
    /// <see cref="ConformanceCheck.Code"/> is documented as a STABLE MACHINE CODE, and a
    /// per-violation row would need a positional one (<c>ubl.xsd.7</c>) that means a different thing
    /// in every report. The count and the first few violations, with line numbers, go in the message
    /// — which is what both renderers show. Do not "improve" this into N checks without first
    /// checking what the frontend panel keys on.</para>
    /// </summary>
    private static void AddSchemaCheck(ConformanceCheckBuilder b, string documentText)
    {
        UblSchemaResult result;
        try
        {
            result = UblSchemaValidator.Validate(documentText);
        }
        catch (Exception ex)
        {
            // A checker must never throw (see IProfileChecker), and this is the only step that
            // could: the schema set is built on first use, and a broken vendored set throws there.
            // Reported as a FAILED check, never as a pass — an unavailable validator that renders
            // as "valid" is the exact failure this whole packet exists to remove.
            b.Add("ubl.xsd", false, SchemaProfileRef,
                "Schema validation could not run, so this document is UNVERIFIED against the OASIS " +
                $"schema — not known-good. {ex.Message}");
            return;
        }

        if (result.Valid)
        {
            // Deliberately does NOT name a profile — not even to disclaim one. The markdown report
            // leaves the product as evidence, and UblOrderDeclaresNoPeppolProfileTests forbids the
            // word in it precisely because a disclaimer beside a PASS still puts the profile's name
            // next to a green result. Say what was checked; say nothing about what was not.
            b.Add("ubl.xsd", true, SchemaProfileRef,
                "Valid against the OASIS UBL 2.1 Order schema — element order, cardinality and " +
                "datatypes. A grammar check, not a statement that the supplier will accept the order.");
            return;
        }

        var shown = result.Findings.Take(3).Select(f => f.ToString());
        var suffix = result.Findings.Count > 3 || result.Truncated ? " …" : string.Empty;

        b.Add("ubl.xsd", false, SchemaProfileRef,
            $"{result.Findings.Count}{(result.Truncated ? "+" : string.Empty)} schema violation(s): " +
            string.Join(" · ", shown) + suffix);
    }

    private const string SchemaProfileRef = "OASIS UBL 2.1 Order-2 XSD";

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        foreach (var (code, @ref) in new[]
                 {
                     ("ubl.root", "Order (root)"),
                     ("ubl.version", "cbc:UBLVersionID"),
                     ("ubl.id", "cbc:ID"),
                     ("ubl.issueDate", "cbc:IssueDate"),
                     ("ubl.currency", "cbc:DocumentCurrencyCode"),
                     ("ubl.buyerParty", "cac:BuyerCustomerParty/.../cbc:Name"),
                     ("ubl.orderLine.cardinality", "cac:OrderLine (1..n)"),
                     ("ubl.lineItem", "cac:OrderLine/cac:LineItem"),
                     ("ubl.lineItem.id", "cac:LineItem/cbc:ID"),
                     ("ubl.lineItem.quantity", "cac:LineItem/cbc:Quantity"),
                     ("ubl.lineItem.item", "cac:LineItem/cac:Item"),
                     ("ubl.xsd", SchemaProfileRef),
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
