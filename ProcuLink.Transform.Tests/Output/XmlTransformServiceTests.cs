using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

public class XmlTransformServiceTests
{
    private static PurchaseOrderEntity BuildOrder(
        string poNumber   = "PO-XML-001",
        string currency   = "EUR",
        DateOnly? date    = null,
        IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
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

    private static PurchaseOrderEntity BuildAddressedOrder()
    {
        var order = BuildOrder();
        order.ContactName      = "REDACTED-NAME";
        order.ContactEmail     = "redacted@example.invalid";
        order.ContactPhone     = "REDACTED-PHONE";
        order.ShipToName       = "REDACTED-PARTY";
        order.ShipToDeliverTo  = "REDACTED-NAME";
        order.ShipToStreet     = "REDACTED-ADDRESS)";
        order.ShipToCity       = "REDACTED-ADDRESS";
        order.ShipToPostalCode = "63040";
        order.ShipToCountry    = "FRANCE";
        order.ShipToEmail      = "redacted@example.invalid";
        order.ShipToPhone      = "REDACTED-PHONE";
        order.BillToName       = "REDACTED-PARTY";
        order.BillToStreet     = "REDACTED-ADDRESS";
        order.BillToCity       = "REDACTED-ADDRESS";
        order.BillToPostalCode = "63000";
        order.BillToCountry    = "FRANCE";
        return order;
    }

    private static async Task<string> ReadContentAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    // ── Routing ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForXmlOnly()
    {
        var svc = new XmlTransformService();
        svc.CanTransform(OutputFormat.Xml).Should().BeTrue();
        svc.CanTransform(OutputFormat.CXml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
        svc.CanTransform(OutputFormat.Json).Should().BeFalse();
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_HappyPath_ProducesWellFormedXml()
    {
        var svc    = new XmlTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Xml, CancellationToken.None);

        result.ContentType.Should().Be("application/xml");
        result.FileExtension.Should().Be(".xml");

        var doc = XDocument.Parse(await ReadContentAsString(result));
        doc.Root!.Name.LocalName.Should().Be("PurchaseOrder");
        doc.Descendants("PoNumber").Single().Value.Should().Be("PO-XML-001");
        doc.Descendants("Line").Should().HaveCount(1);
    }

    // ── Address + contact blocks (mirrors cXML; reuses canonical fields) ─────────

    [Fact]
    public async Task TransformAsync_NoAddressData_EmitsNoAddressBlocks()
    {
        // BYTE-SAFETY LOCK: a default order (no address/contact fields) must emit NO
        // ShipTo / BillTo / Contact elements — existing XML suppliers are byte-unaffected.
        var svc = new XmlTransformService();
        var xml = await ReadContentAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.Xml, CancellationToken.None));

        xml.Should().NotContain("<ShipTo>");
        xml.Should().NotContain("<BillTo>");
        xml.Should().NotContain("<Contact>");
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_EmitsShipToBillToContact_InsideHeader()
    {
        var svc = new XmlTransformService();
        var doc = XDocument.Parse(await ReadContentAsString(
            await svc.TransformAsync(BuildAddressedOrder(), OutputFormat.Xml, CancellationToken.None)));

        var header = doc.Root!.Element("Header")!;

        var shipTo = header.Element("ShipTo")!;
        shipTo.Element("Name")!.Value.Should().Be("REDACTED-PARTY");
        shipTo.Element("DeliverTo")!.Value.Should().Be("REDACTED-NAME");
        shipTo.Element("Street")!.Value.Should().Be("REDACTED-ADDRESS)");
        shipTo.Element("City")!.Value.Should().Be("REDACTED-ADDRESS");
        shipTo.Element("PostalCode")!.Value.Should().Be("63040");
        shipTo.Element("Country")!.Value.Should().Be("FRANCE");
        shipTo.Element("Email")!.Value.Should().Be("redacted@example.invalid");
        shipTo.Element("Phone")!.Value.Should().Be("REDACTED-PHONE");

        var billTo = header.Element("BillTo")!;
        billTo.Element("Name")!.Value.Should().Be("REDACTED-PARTY");
        billTo.Element("PostalCode")!.Value.Should().Be("63000");

        var contact = header.Element("Contact")!;
        contact.Element("Name")!.Value.Should().Be("REDACTED-NAME");
        contact.Element("Email")!.Value.Should().Be("redacted@example.invalid");
        contact.Element("Phone")!.Value.Should().Be("REDACTED-PHONE");

        // Blocks sit inside Header, AFTER SupplierName and BEFORE Lines.
        var headerChildren = doc.Root!.Element("Header")!.Elements().Select(e => e.Name.LocalName).ToList();
        headerChildren.IndexOf("SupplierName").Should().BeLessThan(headerChildren.IndexOf("ShipTo"));
        doc.Root!.Elements().Select(e => e.Name.LocalName).ToList()
            .IndexOf("Header").Should().BeLessThan(
                doc.Root!.Elements().Select(e => e.Name.LocalName).ToList().IndexOf("Lines"));
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_LineNeedsReview_ThrowsTransformValidationException()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, SupplierItemCode = null, Quantity = 1m, UnitPrice = 10m,
                NeedsReview = true, Confidence = 0.5f,
            }
        };

        var svc = new XmlTransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Xml, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }
}
