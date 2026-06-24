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
        result.FileExtension.Should().Be(".cxml");

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
