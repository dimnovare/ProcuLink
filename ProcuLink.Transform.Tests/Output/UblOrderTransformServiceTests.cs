using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class UblOrderTransformServiceTests
{
    // ── UBL 2.1 namespaces (must match transformer) ──────────────────────────
    private static readonly XNamespace UblOrder = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private static readonly XNamespace Cac      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private const string PeppolBisCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";

    private static PurchaseOrderEntity BuildOrder(
        string poNumber   = "PO-UBL-001",
        string currency   = "EUR",
        DateOnly? date    = null,
        Guid? orgId       = null,
        Guid? supplierId  = null,
        IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId      ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = supplierId ?? Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = poNumber,
            OrderDate  = date ?? new DateOnly(2026, 5, 28),
            Currency   = currency,
            Status     = "ready",
        };

        order.Lines = (lines ?? new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "BUYER-001",
                SupplierItemCode = "SUP-ABC-001",
                Description      = "Widget Type A",
                Quantity         = 10m,
                Unit             = "EA",
                UnitPrice        = 125.00m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        }).ToList();

        return order;
    }

    // ── Required tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_GeneratesValidUblDocument_WithCorrectNamespaces()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);

        result.ContentType.Should().Be("application/xml");
        result.FileExtension.Should().Be(".xml");

        var xml = await ReadContentAsString(result);
        var doc = XDocument.Parse(xml); // must be well-formed

        var root = doc.Root!;
        root.Name.LocalName.Should().Be("Order");
        root.Name.NamespaceName.Should().Be(UblOrder.NamespaceName,
            "the default namespace must be the UBL 2.1 Order-2 namespace");

        // cac/cbc namespaces must be declared and resolvable
        var firstCbc = doc.Descendants(Cbc + "UBLVersionID").FirstOrDefault();
        firstCbc.Should().NotBeNull("cbc namespace must be wired and a UBLVersionID element must exist");
        firstCbc!.Value.Should().Be("2.1");

        // SellerSupplierParty (cac) must be present
        var sellerParty = doc.Descendants(Cac + "SellerSupplierParty").FirstOrDefault();
        sellerParty.Should().NotBeNull("cac namespace must be wired and SellerSupplierParty must exist");
    }

    [Fact]
    public async Task TransformAsync_IncludesPeppolBisCustomizationId()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        var customizationId = doc.Descendants(Cbc + "CustomizationID").FirstOrDefault();
        customizationId.Should().NotBeNull();
        customizationId!.Value.Should().Be(PeppolBisCustomizationId);

        var profileId = doc.Descendants(Cbc + "ProfileID").FirstOrDefault();
        profileId.Should().NotBeNull();
        profileId!.Value.Should().Be("urn:fdc:peppol.eu:poacc:bis:order_only:3");
    }

    [Fact]
    public async Task TransformAsync_ThrowsValidationException_WhenUnresolvedLinesPresent()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = null, // unresolved
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = true,
                Confidence       = 0.5f,
            }
        };

        var svc   = new UblOrderTransformService();
        var order = BuildOrder(lines: lines);

        var act = async () => await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var ex  = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.UnresolvedLineNumbers.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task TransformAsync_RoundTripsThroughUblOrderParser()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder(
            poNumber: "PO-ROUNDTRIP-7",
            lines: new[]
            {
                new PurchaseOrderLineEntity
                {
                    LineNumber       = 1,
                    BuyerItemCode    = "B-001",
                    SupplierItemCode = "S-ROUND-001",
                    Description      = "Round-trip widget",
                    Quantity         = 4m,
                    Unit             = "EA",
                    UnitPrice        = 50.00m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    LineNumber       = 2,
                    BuyerItemCode    = "B-002",
                    SupplierItemCode = "S-ROUND-002",
                    Description      = "Round-trip widget 2",
                    Quantity         = 1m,
                    Unit             = "EA",
                    UnitPrice        = 12.50m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                }
            });

        // Emit
        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);

        // Feed the emitted bytes straight into the inbound parser
        result.Content.Position = 0;
        var parser = new UblOrderParser();
        var parsed = await parser.ParseAsync(result.Content, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-ROUNDTRIP-7");

        // First line: BuyerItemCode resolution falls back to seller's item id when
        // no BuyersItemIdentification element is present, so the parser surfaces
        // the supplier item code we emitted.
        parsed.Lines.Should().HaveCount(2);
        var firstParsedLine = parsed.Lines.OrderBy(l => l.LineNumber).First();
        firstParsedLine.BuyerItemCode.Should().Be("S-ROUND-001",
            "the round-trip exposes the SellersItemIdentification ID as the only available identifier");
        firstParsedLine.Quantity.Should().Be(4m);
        firstParsedLine.UnitPrice.Should().Be(50.00m);
        firstParsedLine.Description.Should().Be("Round-trip widget");
    }

    // ── Required-field validation (output-format hardening) ────────────────────

    [Fact]
    public async Task TransformAsync_ZeroUnitPrice_NowTransforms()
    {
        // A €0 line is a legitimately-free line (founder-approved): UBL transforms it, not held.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 0m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new UblOrderTransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Ubl, CancellationToken.None);

        result.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task TransformAsync_NegativeUnitPrice_IsFlaggedForReview()
    {
        // A negative unit price is financially impossible. UBL still holds it for review.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = -5m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new UblOrderTransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Ubl, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.MissingOrZeroPrice);
    }

    // ── Bonus: confirm CanTransform routes correctly ─────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForUblOnly()
    {
        var svc = new UblOrderTransformService();
        svc.CanTransform(OutputFormat.Ubl).Should().BeTrue();
        svc.CanTransform(OutputFormat.CXml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
        svc.CanTransform(OutputFormat.Json).Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadContentAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }
}
