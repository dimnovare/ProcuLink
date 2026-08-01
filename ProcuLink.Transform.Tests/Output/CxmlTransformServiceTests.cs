using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

public class CxmlTransformServiceTests
{
    private static PurchaseOrderEntity BuildOrder(
        string poNumber   = "PO-12345",
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
            OrderDate  = date ?? new DateOnly(2024, 1, 15),
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

    // ── CanTransform ──────────────────────────────────────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForCXmlOnly()
    {
        var svc = new CxmlTransformService();
        svc.CanTransform(OutputFormat.CXml).Should().BeTrue();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_HappyPath_ProducesWellFormedXml()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);

        result.ContentType.Should().Be("application/xml");
        // WP-20: .xml, not .cxml. cXML IS an XML document; ".cxml" is registered nowhere and is
        // rejected by receivers that filter on extension. There is now ONE source of truth for
        // this (DeliveryMediaTypes) and the transform reads from it, so the stored artifact and
        // the delivered file can no longer disagree about what the document is called.
        result.FileExtension.Should().Be(".xml");

        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        var xml = await reader.ReadToEndAsync();

        var doc = XDocument.Parse(xml); // should not throw
        doc.Root!.Name.LocalName.Should().Be("cXML");
    }

    [Fact]
    public async Task TransformAsync_HappyPath_ContainsCorrectOrderId()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder(poNumber: "PO-UNIT-TEST");

        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        var header = doc.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "OrderRequestHeader",
                                               StringComparison.OrdinalIgnoreCase));

        header.Should().NotBeNull();
        header!.Attribute("orderID")?.Value.Should().Be("PO-UNIT-TEST");
    }

    [Fact]
    public async Task TransformAsync_LineItemsMappedCorrectly()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        var itemOuts = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "ItemOut", StringComparison.OrdinalIgnoreCase))
            .ToList();

        itemOuts.Should().HaveCount(1);

        var itemOut = itemOuts[0];
        itemOut.Attribute("quantity")?.Value.Should().Be("10");
        itemOut.Attribute("lineNumber")?.Value.Should().Be("1");

        var supplierPartId = itemOut.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "SupplierPartID",
                                               StringComparison.OrdinalIgnoreCase))?.Value;
        supplierPartId.Should().Be("SUP-ABC-001");
    }

    [Fact]
    public async Task TransformAsync_CurrencyCarriesThroughToMoney()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder(currency: "USD");

        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        var moneyElements = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Money", StringComparison.OrdinalIgnoreCase))
            .ToList();

        moneyElements.Should().NotBeEmpty();
        moneyElements.All(m => m.Attribute("currency")?.Value == "USD").Should().BeTrue();
    }

    [Fact]
    public async Task TransformAsync_MultipleLines_EmitsOneItemOutPerLine()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = "S-001",
                Description      = "First",
                Quantity         = 5m,
                Unit             = "EA",
                UnitPrice        = 10m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber       = 2,
                BuyerItemCode    = "B-002",
                SupplierItemCode = "S-002",
                Description      = "Second",
                Quantity         = 3m,
                Unit             = "KG",
                UnitPrice        = 20m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            },
        };

        var svc    = new CxmlTransformService();
        var order  = BuildOrder(lines: lines);
        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        var itemOuts = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "ItemOut", StringComparison.OrdinalIgnoreCase))
            .ToList();

        itemOuts.Should().HaveCount(2);
        itemOuts[0].Attribute("lineNumber")?.Value.Should().Be("1");
        itemOuts[1].Attribute("lineNumber")?.Value.Should().Be("2");
    }

    [Fact]
    public async Task TransformAsync_PayloadIdIsUniqueGuid()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder();

        var r1 = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        var r2 = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);

        var xml1 = await ReadContentAsString(r1);
        var xml2 = await ReadContentAsString(r2);

        var payloadId1 = XDocument.Parse(xml1).Root!.Attribute("payloadID")?.Value;
        var payloadId2 = XDocument.Parse(xml2).Root!.Attribute("payloadID")?.Value;

        payloadId1.Should().NotBeNullOrEmpty();
        payloadId2.Should().NotBeNullOrEmpty();
        payloadId1.Should().NotBe(payloadId2, "each cXML document must have a unique payloadID");
    }

    // ── Address blocks (ShipTo / BillTo / Contact) + header dateTime ──────────

    [Fact]
    public async Task TransformAsync_NoAddressData_EmitsNoAddressBlocks()
    {
        // BYTE-SAFETY LOCK: an order with no ShipTo*/BillTo*/Contact* fields set (the default
        // BuildOrder()) must emit NO address blocks at all — existing cXML suppliers are unaffected.
        var svc   = new CxmlTransformService();
        var order = BuildOrder();

        var xml = await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None));

        xml.Should().NotContain("<ShipTo");
        xml.Should().NotContain("<BillTo");
        xml.Should().NotContain("<Contact");
    }

    [Fact]
    public async Task TransformAsync_OrderDate_IsDateTimeMidnight()
    {
        // cXML orderDate is an ISO-8601 dateTime; a DateOnly renders at midnight.
        var svc   = new CxmlTransformService();
        var order = BuildOrder(); // OrderDate = 2024-01-15

        var xml = await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None));
        var doc = XDocument.Parse(xml);

        var header = doc.Descendants()
            .First(e => string.Equals(e.Name.LocalName, "OrderRequestHeader", StringComparison.OrdinalIgnoreCase));

        header.Attribute("orderDate")?.Value.Should().Be("2024-01-15T00:00:00");
    }

    [Fact]
    public async Task TransformAsync_Header_HasOrderVersion1()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder();

        var xml = await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None));
        var doc = XDocument.Parse(xml);

        var header = doc.Descendants()
            .First(e => string.Equals(e.Name.LocalName, "OrderRequestHeader", StringComparison.OrdinalIgnoreCase));

        header.Attribute("orderVersion")?.Value.Should().Be("1");
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_EmitsShipToBillToContact()
    {
        var svc   = new CxmlTransformService();
        var order = BuildOrder();
        // REDACTED-PARTY-shaped address data.
        order.ShipToName       = "REDACTED-PARTY";
        order.ShipToDeliverTo  = "REDACTED-NAME";
        order.ShipToStreet     = "REDACTED-ADDRESS)";
        order.ShipToCity       = "REDACTED-ADDRESS";
        order.ShipToPostalCode = "63040";
        order.ShipToCountry    = "FRANCE";
        order.ShipToPhone      = "REDACTED-PHONE";
        order.BillToName       = "REDACTED-PARTY";
        order.BillToDeliverTo  = "Service Comptable";
        order.BillToStreet     = "REDACTED-ADDRESS";
        order.BillToCity       = "REDACTED-ADDRESS";
        order.BillToPostalCode = "63000";
        order.BillToCountry    = "FRANCE";
        order.BillToPhone      = "REDACTED-PHONE";
        order.ContactName      = "REDACTED-NAME";
        order.ContactEmail     = "redacted@example.invalid";
        order.ContactPhone     = "REDACTED-PHONE";

        var xml = await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None));
        var doc = XDocument.Parse(xml);
        XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";

        var shipTo = doc.Descendants()
            .First(e => string.Equals(e.Name.LocalName, "ShipTo", StringComparison.OrdinalIgnoreCase));
        var shipAddress = shipTo.Elements().First(e => e.Name.LocalName == "Address");

        // ShipTo/Address/Name with xml:lang="en".
        var shipName = shipAddress.Elements().First(e => e.Name.LocalName == "Name");
        shipName.Value.Should().Be("REDACTED-PARTY");
        shipName.Attribute(xmlNs + "lang")?.Value.Should().Be("en");

        // ShipTo/Address/PostalAddress/{DeliverTo,Street,City,PostalCode,Country}.
        var postal = shipAddress.Elements().First(e => e.Name.LocalName == "PostalAddress");
        postal.Elements().First(e => e.Name.LocalName == "DeliverTo").Value.Should().Be("REDACTED-NAME");
        postal.Elements().First(e => e.Name.LocalName == "Street").Value.Should().Be("REDACTED-ADDRESS)");
        postal.Elements().First(e => e.Name.LocalName == "City").Value.Should().Be("REDACTED-ADDRESS");
        postal.Elements().First(e => e.Name.LocalName == "PostalCode").Value.Should().Be("63040");
        postal.Elements().First(e => e.Name.LocalName == "Country").Value.Should().Be("FRANCE");

        // ShipTo/Address/Phone/TelephoneNumber/Number.
        var shipNumber = shipAddress.Descendants().First(e => e.Name.LocalName == "Number");
        shipNumber.Value.Should().Be("REDACTED-PHONE");

        // BillTo present with its own address name.
        var billTo = doc.Descendants()
            .First(e => string.Equals(e.Name.LocalName, "BillTo", StringComparison.OrdinalIgnoreCase));
        billTo.Descendants().First(e => e.Name.LocalName == "Name").Value
            .Should().Be("REDACTED-PARTY");

        // Contact/{Name,Email,Phone}.
        var contact = doc.Descendants()
            .First(e => string.Equals(e.Name.LocalName, "Contact", StringComparison.OrdinalIgnoreCase));
        contact.Elements().First(e => e.Name.LocalName == "Name").Value.Should().Be("REDACTED-NAME");
        contact.Elements().First(e => e.Name.LocalName == "Email").Value.Should().Be("redacted@example.invalid");
        contact.Descendants().First(e => e.Name.LocalName == "Number").Value.Should().Be("REDACTED-PHONE");

        // Position: ShipTo / BillTo / Contact sit AFTER <Total> and BEFORE the first <ItemOut>.
        var totalIdx   = xml.IndexOf("<Total", StringComparison.Ordinal);
        var shipToIdx  = xml.IndexOf("<ShipTo", StringComparison.Ordinal);
        var billToIdx  = xml.IndexOf("<BillTo", StringComparison.Ordinal);
        var contactIdx = xml.IndexOf("<Contact", StringComparison.Ordinal);
        var itemOutIdx = xml.IndexOf("<ItemOut", StringComparison.Ordinal);

        totalIdx.Should().BeGreaterThanOrEqualTo(0);
        shipToIdx.Should().BeGreaterThan(totalIdx);
        billToIdx.Should().BeGreaterThan(shipToIdx);
        contactIdx.Should().BeGreaterThan(billToIdx);
        itemOutIdx.Should().BeGreaterThan(contactIdx);
    }

    // ── Validation errors ─────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_LineNeedsReview_ThrowsTransformValidationException()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = null,
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = true,
                Confidence       = 0.5f,
            }
        };

        var svc   = new CxmlTransformService();
        var order = BuildOrder(lines: lines);

        var act = async () => await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }

    [Fact]
    public async Task TransformAsync_NullSupplierItemCode_ThrowsTransformValidationException()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = null,
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        };

        var svc   = new CxmlTransformService();
        var order = BuildOrder(lines: lines);

        var act = async () => await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);
        await act.Should().ThrowAsync<TransformValidationException>();
    }

    [Fact]
    public async Task TransformAsync_ZeroUnitPrice_NowTransforms()
    {
        // A €0 line is a legitimately-free line (founder-approved): cXML transforms it, not held.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = "SUP-1",
                Description      = "Widget",
                Quantity         = 1m,
                Unit             = "EA",
                UnitPrice        = 0m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        };

        var svc   = new CxmlTransformService();
        var order = BuildOrder(lines: lines);

        var result = await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);

        result.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task TransformAsync_NegativeUnitPrice_IsFlaggedForReview()
    {
        // A negative unit price is financially impossible. cXML still holds it for review.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = "SUP-1",
                Description      = "Widget",
                Quantity         = 1m,
                Unit             = "EA",
                UnitPrice        = -5m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        };

        var svc   = new CxmlTransformService();
        var order = BuildOrder(lines: lines);

        var act = async () => await svc.TransformAsync(order, OutputFormat.CXml, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.MissingOrZeroPrice);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadContentAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }
}
