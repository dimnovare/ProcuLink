using System.Xml.Linq;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a SAP IDoc <c>ORDERS05</c> purchase-order document against the named
/// ORDERS05 profile. ProcuLink consumes ORDERS05 inbound (no outbound IDoc
/// transform yet), so this checker asserts the same structural guarantees the
/// inbound <c>IDocOrders05Parser</c> requires — making the round-trip contract
/// explicit and downloadable. Pragmatic structural + mandatory-segment + cardinality
/// checks per the ORDERS05 basic type — not a full IDoc metadata validator.
/// </summary>
internal sealed class IDocProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.SapIDocOrders05;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "SAP IDoc ORDERS05", "ORDERS05");

        XDocument? doc = null;
        var wellFormed = false;
        try
        {
            doc = XDocument.Parse(documentText);
            wellFormed = true;
        }
        catch (System.Xml.XmlException ex)
        {
            b.Add("idoc.wellformed", false, "XML 1.0",
                $"Document is not well-formed XML: {ex.Message}");
        }

        if (!wellFormed || doc?.Root is null)
        {
            b.Add("idoc.wellformed", wellFormed, "XML 1.0",
                wellFormed ? "Document is well-formed XML." : "Document has no root element.");
            FailRemaining(b);
            return b.Build();
        }

        b.Add("idoc.wellformed", true, "XML 1.0", "Document is well-formed XML.");
        var root = doc.Root;

        b.Require("idoc.root", string.Equals(root.Name.LocalName, "ORDERS05", StringComparison.OrdinalIgnoreCase),
            "ORDERS05 (root)", "Root element is <ORDERS05>.",
            $"Root element must be <ORDERS05> but was <{root.Name.LocalName}>.");

        // ── IDOC segment ───────────────────────────────────────────────────────
        var idoc = Descendants(root, "IDOC").FirstOrDefault();
        b.Require("idoc.idoc", idoc is not null, "ORDERS05/IDOC",
            "Mandatory <IDOC> segment present.", "Mandatory <IDOC> segment is missing.");

        // ── Header: E1EDK01, and a document number from E1EDK02 BELNR / E1EDK01 BELNR ──
        var edk01 = idoc is null ? null : Children(idoc, "E1EDK01").FirstOrDefault();
        b.Require("idoc.e1edk01", edk01 is not null, "IDOC/E1EDK01 (document header)",
            "E1EDK01 document header present.", "Mandatory E1EDK01 document header is missing.");

        var edk02List = idoc is null ? new List<XElement>() : Children(idoc, "E1EDK02").ToList();
        var poFromEdk02 = edk02List
            .Select(e => ChildValue(e, "BELNR"))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var poFromEdk01 = ChildValue(edk01, "BELNR");
        var poNumber = poFromEdk02 ?? poFromEdk01;
        b.Require("idoc.poNumber", !string.IsNullOrWhiteSpace(poNumber), "E1EDK02/BELNR or E1EDK01/BELNR",
            "Document (PO) number present.", "Mandatory document number (E1EDK02 BELNR or E1EDK01 BELNR) is missing.");

        // ── Lines: ≥1 E1EDP01, each with a usable item identifier ──────────────
        var lineEls = idoc is null ? new List<XElement>() : Children(idoc, "E1EDP01").ToList();
        b.Require("idoc.e1edp01.cardinality", lineEls.Count >= 1, "IDOC/E1EDP01 (item segment, 1..n)",
            $"{lineEls.Count} E1EDP01 line segment(s) present.", "At least one E1EDP01 line segment is required.");

        var allLinesHaveItem = lineEls.Count > 0 && lineEls.All(HasItemIdentifier);
        b.Require("idoc.e1edp01.itemId", allLinesHaveItem, "E1EDP01/E1EDP19/IDTNR or E1EDP01/POSEX",
            "Every E1EDP01 line carries a usable item identifier (E1EDP19 IDTNR or POSEX).",
            "An E1EDP01 line is missing a usable item identifier (E1EDP19 IDTNR or POSEX).");

        return b.Build();
    }

    /// <summary>An E1EDP01 line carries an item identifier from any E1EDP19/IDTNR, else POSEX.</summary>
    private static bool HasItemIdentifier(XElement lineEl)
    {
        var fromIdtnr = Children(lineEl, "E1EDP19")
            .Select(e => ChildValue(e, "IDTNR"))
            .Any(v => !string.IsNullOrWhiteSpace(v));
        if (fromIdtnr) return true;
        return !string.IsNullOrWhiteSpace(ChildValue(lineEl, "POSEX"));
    }

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        foreach (var (code, @ref) in new[]
                 {
                     ("idoc.root", "ORDERS05 (root)"),
                     ("idoc.idoc", "ORDERS05/IDOC"),
                     ("idoc.e1edk01", "IDOC/E1EDK01"),
                     ("idoc.poNumber", "E1EDK02/BELNR or E1EDK01/BELNR"),
                     ("idoc.e1edp01.cardinality", "IDOC/E1EDP01 (1..n)"),
                     ("idoc.e1edp01.itemId", "E1EDP01/E1EDP19/IDTNR or POSEX"),
                 })
            b.Add(code, false, @ref, "Not checked — document is not well-formed.");
    }

    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent is null
            ? Enumerable.Empty<XElement>()
            : parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Descendants(XElement parent, string localName) =>
        parent.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static string? ChildValue(XElement? parent, string localName) =>
        Children(parent, localName).FirstOrDefault()?.Value?.Trim();
}
