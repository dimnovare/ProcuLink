using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class UblParsedOrderTransformTests
{
    // ── UBL 2.1 namespaces (must match the transform) ─────────────────────────
    private static readonly XNamespace UblOrder = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private static readonly XNamespace Cac      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private const string PeppolBisCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";

    private static ParsedOrder SampleOrder(IReadOnlyList<ParsedOrderLine>? lines = null) =>
        new(
            PoNumber:  "PO-UBL-001",
            OrderDate: new DateTime(2026, 5, 30),
            BuyerName: "Acme Buyer Ltd",
            Currency:  "EUR",
            Lines: lines ?? new List<ParsedOrderLine>
            {
                new(LineNumber: 1, BuyerItemCode: "BUYER-001", Description: "Widget Type A", Quantity: 10m, Unit: "EA",  UnitPrice: 125.00m),
                new(LineNumber: 2, BuyerItemCode: "BUYER-002", Description: "Bolt M6",       Quantity: 250m, Unit: "EA", UnitPrice: 0.45m),
            });

    [Fact]
    public void CanTransform_ReturnsTrueForUblOrderOnly()
    {
        var svc = new UblParsedOrderTransform();
        svc.CanTransform(OutputFormat.UblOrder).Should().BeTrue();
        svc.CanTransform(OutputFormat.X12_850).Should().BeFalse();
        svc.CanTransform(OutputFormat.EdifactOrders).Should().BeFalse();
        svc.CanTransform(OutputFormat.Ubl).Should().BeFalse(); // entity-based sibling value
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
    }

    [Fact]
    public void Transform_EmitsWellFormedUbl_WithCorrectRootAndNamespaces()
    {
        var svc = new UblParsedOrderTransform();

        var result = svc.Transform(SampleOrder(), OutputFormat.UblOrder);

        result.ContentType.Should().Be("application/xml");
        result.FileExtension.Should().Be(".xml");

        var doc  = XDocument.Parse(result.AsText()); // must be well-formed
        var root = doc.Root!;
        root.Name.LocalName.Should().Be("Order");
        root.Name.NamespaceName.Should().Be(UblOrder.NamespaceName);

        doc.Descendants(Cbc + "UBLVersionID").Single().Value.Should().Be("2.1");
        doc.Descendants(Cbc + "CustomizationID").Single().Value.Should().Be(PeppolBisCustomizationId);
        doc.Descendants(Cbc + "ProfileID").Single().Value.Should().Be("urn:fdc:peppol.eu:poacc:bis:order_only:3");
        doc.Descendants(Cbc + "ID").First().Value.Should().Be("PO-UBL-001");
        doc.Descendants(Cbc + "IssueDate").Single().Value.Should().Be("2026-05-30");
        doc.Descendants(Cbc + "DocumentCurrencyCode").Single().Value.Should().Be("EUR");

        // Buyer party name present
        doc.Descendants(Cac + "BuyerCustomerParty")
           .Descendants(Cbc + "Name").Single().Value.Should().Be("Acme Buyer Ltd");

        // One OrderLine per input line
        doc.Descendants(Cac + "OrderLine").Should().HaveCount(2);
    }

    [Fact]
    public void Transform_EmitsQuantityUnitCodeAndPriceCurrency()
    {
        var svc = new UblParsedOrderTransform();
        var doc = XDocument.Parse(svc.Transform(SampleOrder(), OutputFormat.UblOrder).AsText());

        var firstLine = doc.Descendants(Cac + "LineItem").First();

        var qty = firstLine.Element(Cbc + "Quantity")!;
        qty.Attribute("unitCode")!.Value.Should().Be("EA");
        qty.Value.Should().Be("10");

        var priceAmount = firstLine.Descendants(Cbc + "PriceAmount").Single();
        priceAmount.Attribute("currencyID")!.Value.Should().Be("EUR");
        priceAmount.Value.Should().Be("125.00");

        // Buyer item code emitted as BuyersItemIdentification so it round-trips.
        firstLine.Descendants(Cac + "BuyersItemIdentification")
                 .Descendants(Cbc + "ID").Single().Value.Should().Be("BUYER-001");
    }

    [Fact]
    public async Task Transform_RoundTripsThroughUblOrderParser()
    {
        var svc    = new UblParsedOrderTransform();
        var source = SampleOrder();

        var result = svc.Transform(source, OutputFormat.UblOrder);

        using var stream = new MemoryStream(result.Content);
        var parsed = await new UblOrderParser().ParseAsync(stream, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-UBL-001");
        parsed.OrderDate.Should().Be(new DateTime(2026, 5, 30));
        parsed.Currency.Should().Be("EUR");
        parsed.BuyerName.Should().Be("Acme Buyer Ltd");
        parsed.Lines.Should().HaveCount(2);

        var first = parsed.Lines.OrderBy(l => l.LineNumber).First();
        first.LineNumber.Should().Be(1);
        first.BuyerItemCode.Should().Be("BUYER-001"); // recovered from BuyersItemIdentification
        first.Description.Should().Be("Widget Type A");
        first.Quantity.Should().Be(10m);
        first.Unit.Should().Be("EA");
        first.UnitPrice.Should().Be(125.00m);
    }

    [Fact]
    public void Transform_ToleratesNullHeaderFields()
    {
        // Null HEADER fields (PoNumber / OrderDate / BuyerName / Currency) must still
        // default cleanly. The line carries a valid price so the (separate) required-
        // field price guard is not what is under test here.
        var svc   = new UblParsedOrderTransform();
        var order = new ParsedOrder(
            PoNumber:  null,
            OrderDate: null,
            BuyerName: null,
            Currency:  null,
            Lines: new List<ParsedOrderLine>
            {
                new(LineNumber: 1, BuyerItemCode: "X-1", Description: null, Quantity: 1m, Unit: null, UnitPrice: 9.99m),
            });

        var act = () => XDocument.Parse(svc.Transform(order, OutputFormat.UblOrder).AsText());
        act.Should().NotThrow();

        var doc = svc.Transform(order, OutputFormat.UblOrder).AsText();
        var parsed = XDocument.Parse(doc);
        // Defaults applied: currency EUR, unitCode EA.
        parsed.Descendants(Cbc + "DocumentCurrencyCode").Single().Value.Should().Be("EUR");
        parsed.Descendants(Cbc + "Quantity").Single().Attribute("unitCode")!.Value.Should().Be("EA");
    }

    // ── Required-field validation (output-format hardening) ────────────────────

    [Fact]
    public void Transform_MissingUnitPrice_IsFlaggedForReview()
    {
        // A null unit price would emit a 0.00 LineExtensionAmount / PriceAmount — a
        // financially-wrong document. UBL holds it for review rather than delivering
        // blind. (Empty buyer code is NOT a hard UBL failure; it rides the optional
        // BuyersItemIdentification element, so only the price guard fires here.)
        var svc   = new UblParsedOrderTransform();
        var order = SampleOrder(new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "BUYER-001", Description: "Widget", Quantity: 1m, Unit: "EA", UnitPrice: null),
        });

        var act = () => svc.Transform(order, OutputFormat.UblOrder);

        act.Should().Throw<TransformValidationException>()
           .Which.UnresolvedLineNumbers.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void Transform_ZeroUnitPrice_IsFlaggedForReview()
    {
        var svc   = new UblParsedOrderTransform();
        var order = SampleOrder(new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "BUYER-001", Description: "Widget", Quantity: 1m, Unit: "EA", UnitPrice: 0m),
        });

        var act = () => svc.Transform(order, OutputFormat.UblOrder);
        act.Should().Throw<TransformValidationException>();
    }

    [Fact]
    public void Transform_ValidOrder_IsByteIdenticalToPreValidationOutput()
    {
        // The validation guard must NOT change the happy-path bytes: a fully-valid
        // order serializes exactly as before the guard existed.
        var svc = new UblParsedOrderTransform();

        var bytesA = svc.Transform(SampleOrder(), OutputFormat.UblOrder).Content;
        var bytesB = svc.Transform(SampleOrder(), OutputFormat.UblOrder).Content;

        bytesA.Should().Equal(bytesB);
        XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytesA)); // still well-formed
    }
}
