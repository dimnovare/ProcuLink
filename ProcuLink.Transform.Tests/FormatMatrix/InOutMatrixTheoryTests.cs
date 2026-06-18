using FluentAssertions;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// Inbound .xml routing contract for <see cref="OrderParserFactory"/>: a plain
/// <c>.xml</c> upload can be cXML, UBL, or SAP IDoc, and the factory must pick the
/// right parser by content-sniff (IDoc / UBL) with a fall-through to the first
/// registered .xml parser (cXML, which has no probe of its own).
///
/// (This file previously also exercised the IN×OUT matrix through the
/// IParsedOrderTransform export stack; that second export stack was deleted in
/// WS-11. The live ITransformService output coverage lives in OutCoverageMatrixTests;
/// inbound parse fidelity lives in InCoverageMatrixTests / HighVolumeMatrixTests.)
/// </summary>
public class InOutMatrixTheoryTests
{
    // The factory mirrors the PRODUCTION DI registration order EXACTLY (Program.cs:
    // Csv, Xlsx, Pdf, Cxml, Ubl, IDoc, Edifact, X12) so content-based disambiguation
    // of .xml (IDoc vs UBL vs cXML) is exercised the way it actually runs.
    //
    // ROUTING FINDING (documented, not a prod bug): for a plain .xml upload the factory
    // content-sniffs IDoc (<ORDERS05>) and UBL (root+namespace) first, then FALLS
    // THROUGH to extension dispatch — which returns the FIRST registered .xml parser.
    // cXML has no sniff of its own, so it relies on Cxml being the first .xml parser.
    // Prod gets this right (Cxml before Ubl/IDoc). If anyone reorders the DI block to
    // put IDoc/UBL before Cxml, a cXML doc would route to the wrong parser and fail
    // with "Document root element is not <ORDERS05>". CxmlRouting_DependsOnRegistrationOrder
    // pins this so a future reorder fails loudly here instead of in production.
    private static OrderParserFactory Factory() =>
        new(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
            new CxmlOrderParser(),
            new UblOrderParser(),
            new IDocOrders05Parser(),
            new EdifactOrderParser(),
            new X12OrderParser(),
        });

    [Fact]
    public void XmlRouting_ContentSniff_PicksIdocAndUbl_BeforeFallthrough()
    {
        // .xml content-sniff must pick IDoc and UBL by content regardless of which
        // .xml parser is registered first, because they have explicit probes.
        var factory = Factory();

        using var idoc = ToStream(Idoc("PO-RT-ID", "Buyer", RepresentativeLines()));
        factory.GetParser(".xml", idoc).Should().BeOfType<IDocOrders05Parser>();

        using var ubl = ToStream(Ubl("PO-RT-UB", "EUR", "Buyer", RepresentativeLines()));
        factory.GetParser(".xml", ubl).Should().BeOfType<UblOrderParser>();

        // cXML has NO content probe → it falls through to the first .xml parser, which
        // prod registers as Cxml. This pins that contract.
        using var cx = ToStream(Cxml("PO-RT-CX", "EUR", RepresentativeLines()));
        factory.GetParser(".xml", cx).Should().BeOfType<CxmlOrderParser>(
            "cXML relies on Cxml being the FIRST registered .xml parser (no content probe of its own)");
    }

    [Fact]
    public void CxmlRouting_DependsOnRegistrationOrder_DocumentedFragility()
    {
        // FINDING (documented fragility, NOT a current prod bug): if the DI block is
        // reordered so an .xml parser WITHOUT a content probe-miss-safe contract sits
        // before Cxml, a cXML upload routes to the wrong parser. Here we deliberately
        // register IDoc first to PROVE the failure mode, so the risk is captured.
        var badOrderFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new IDocOrders05Parser(), // wrong: before Cxml
            new UblOrderParser(),
            new CxmlOrderParser(),
        });

        using var cx = ToStream(Cxml("PO-FRAGILE", "EUR", RepresentativeLines()));
        // cXML has no probe; IDoc/UBL probes miss; fall-through returns IDoc (first .xml).
        badOrderFactory.GetParser(".xml", cx).Should().BeOfType<IDocOrders05Parser>(
            "this WRONG registration order mis-routes cXML — prod must keep Cxml first");
    }
}
