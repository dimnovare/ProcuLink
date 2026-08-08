using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

public class JsonTransformServiceTests
{
    private static PurchaseOrderEntity BuildOrder(
        string poNumber  = "PO-JSON-001",
        string currency  = "EUR",
        DateOnly? date   = null,
        Guid? orgId      = null,
        Guid? supplierId = null,
        IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId      ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = supplierId ?? Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = poNumber,
            OrderDate  = date ?? new DateOnly(2024, 3, 20),
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
                UnitPrice        = 25.00m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        }).ToList();

        return order;
    }

    private static async Task<JsonDocument> ReadJson(TransformResult result)
    {
        result.Content.Position = 0;
        return await JsonDocument.ParseAsync(result.Content);
    }

    // ── CanTransform ──────────────────────────────────────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForJsonOnly()
    {
        var svc = new JsonTransformService();
        svc.CanTransform(OutputFormat.Json).Should().BeTrue();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
        svc.CanTransform(OutputFormat.CXml).Should().BeFalse();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_HappyPath_ContentTypeAndExtensionCorrect()
    {
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);

        result.ContentType.Should().Be("application/json");
        result.FileExtension.Should().Be(".json");
    }

    [Fact]
    public async Task TransformAsync_HappyPath_ProducesValidJson()
    {
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);

        var act = async () => await ReadJson(result);
        await act.Should().NotThrowAsync("the output must be well-formed JSON");
    }

    [Fact]
    public async Task TransformAsync_HeaderFieldsMappedCorrectly()
    {
        var svc   = new JsonTransformService();
        var order = BuildOrder(poNumber: "PO-HEADER-TEST", currency: "USD", date: new DateOnly(2024, 6, 15));

        var result = await svc.TransformAsync(order, OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);
        var root   = doc.RootElement;

        root.GetProperty("poNumber").GetString().Should().Be("PO-HEADER-TEST");
        root.GetProperty("currency").GetString().Should().Be("USD");
        root.GetProperty("orderDate").GetString().Should().Be("2024-06-15");
    }

    [Fact]
    public async Task TransformAsync_LinesEmittedInLineNumberOrder()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 2,
                BuyerItemCode    = "B-002",
                SupplierItemCode = "S-002",
                Description      = "Second",
                Quantity         = 3m,
                Unit             = "KG",
                UnitPrice        = 15m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            },
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
        };

        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        var linesArr = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        linesArr.Should().HaveCount(2);
        linesArr[0].GetProperty("lineNumber").GetInt32().Should().Be(1);
        linesArr[1].GetProperty("lineNumber").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task TransformAsync_LineFieldsMappedCorrectly()
    {
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        var line = doc.RootElement.GetProperty("lines").EnumerateArray().First();
        line.GetProperty("lineNumber").GetInt32().Should().Be(1);
        line.GetProperty("supplierItemCode").GetString().Should().Be("SUP-ABC-001");
        line.GetProperty("buyerItemCode").GetString().Should().Be("BUYER-001");
        line.GetProperty("description").GetString().Should().Be("Widget Type A");
        line.GetProperty("quantity").GetDecimal().Should().Be(10m);
        line.GetProperty("unit").GetString().Should().Be("EA");
        line.GetProperty("unitPrice").GetDecimal().Should().Be(25.00m);
    }

    [Fact]
    public async Task TransformAsync_LineTotalCalculatedCorrectly()
    {
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        var line = doc.RootElement.GetProperty("lines").EnumerateArray().First();
        // 10 * 25 = 250
        line.GetProperty("lineTotal").GetDecimal().Should().Be(250m);
    }

    [Fact]
    public async Task TransformAsync_TotalValueSumsAllLines()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, SupplierItemCode = "S-001",
                Quantity = 4m, UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, SupplierItemCode = "S-002",
                Quantity = 2m, UnitPrice = 30m,
                NeedsReview = false, Confidence = 1.0f,
            },
        };

        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        // (4 * 10) + (2 * 30) = 40 + 60 = 100
        doc.RootElement.GetProperty("totalValue").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task TransformAsync_GeneratedAtIsRecentIsoUtcTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        var generatedAt = doc.RootElement.GetProperty("generatedAt").GetString();
        generatedAt.Should().NotBeNullOrEmpty();

        var parsed = DateTime.Parse(generatedAt!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        parsed.Should().BeAfter(before);
        parsed.Should().BeBefore(DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task TransformAsync_ContentStreamPositionedAtZero()
    {
        var svc    = new JsonTransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None);

        result.Content.Position.Should().Be(0, "stream must be ready to read from the beginning");
    }

    // ── Buyer name resolution (denormalisation split regression) ───────────────
    // BuyerName lives in BOTH the purchase_orders.buyer_name COLUMN and CanonicalJson,
    // written by different code paths: the async parse path updates the COLUMN only and
    // leaves CanonicalJson without a buyerName. The fixed JSON transform must therefore
    // read the column FIRST and only fall back to CanonicalJson — otherwise a correctly
    // extracted buyer is delivered as "" (prod order 14c340a1, 2026-06-13).

    [Fact]
    public async Task TransformAsync_BuyerName_PrefersColumn_WhenCanonicalJsonLacksIt()
    {
        var order = BuildOrder();
        order.BuyerName     = "Exemplar Stahl GmbH";
        order.CanonicalJson = JsonDocument.Parse("""{"poNumber":"PO-JSON-001"}"""); // no buyerName key

        var result = await new JsonTransformService().TransformAsync(order, OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        doc.RootElement.GetProperty("buyerName").GetString()
            .Should().Be("Exemplar Stahl GmbH", "the buyer_name column is authoritative when CanonicalJson omits it");
    }

    [Fact]
    public async Task TransformAsync_BuyerName_FallsBackToCanonicalJson_WhenColumnEmpty()
    {
        var order = BuildOrder();
        order.BuyerName     = null;
        order.CanonicalJson = JsonDocument.Parse("""{"buyerName":"Northwind Trading OÜ"}""");

        var result = await new JsonTransformService().TransformAsync(order, OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        doc.RootElement.GetProperty("buyerName").GetString().Should().Be("Northwind Trading OÜ");
    }

    [Fact]
    public async Task TransformAsync_BuyerName_ColumnWins_OverCanonicalJson()
    {
        var order = BuildOrder();
        order.BuyerName     = "Column Buyer";
        order.CanonicalJson = JsonDocument.Parse("""{"buyerName":"Stale Canonical Buyer"}""");

        var result = await new JsonTransformService().TransformAsync(order, OutputFormat.Json, CancellationToken.None);
        var doc    = await ReadJson(result);

        doc.RootElement.GetProperty("buyerName").GetString().Should().Be("Column Buyer");
    }

    // ── Address + contact blocks (mirrors cXML; reuses canonical fields) ────────

    private static PurchaseOrderEntity BuildAddressedOrder()
    {
        var order = BuildOrder();
        order.ContactName      = "Testperson Alex";
        order.ContactEmail     = "alex.testperson@buyer.example.com";
        order.ContactPhone     = "33100000000";
        order.ShipToName       = "Usine EXEMPLE de la REDACTED-PARTY";
        order.ShipToDeliverTo  = "Testperson Alex";
        order.ShipToStreet     = "12 rue des Essais B12-3 (CTX_0000)";
        order.ShipToCity       = "VILLE-EXEMPLE";
        order.ShipToPostalCode = "63040";
        order.ShipToCountry    = "FRANCE";
        order.ShipToEmail      = "ship@buyer.example.com";
        order.ShipToPhone      = "33100000000";
        order.BillToName       = "EXEMPLE Comptabilite Fournisseurs";
        order.BillToStreet     = "Place des Essais Nord";
        order.BillToCity       = "VILLE-EXEMPLE";
        order.BillToPostalCode = "63000";
        order.BillToCountry    = "FRANCE";
        return order;
    }

    [Fact]
    public async Task TransformAsync_NoAddressData_OmitsShipToBillToContact()
    {
        // BYTE-SAFETY LOCK: a default order (no address/contact fields) must NOT carry shipTo /
        // billTo / contact keys at all — existing JSON suppliers see byte-identical payloads. The
        // conditional-object strategy keeps existing always-emitted null fields (currency) intact.
        var svc = new JsonTransformService();
        var doc = await ReadJson(await svc.TransformAsync(BuildOrder(), OutputFormat.Json, CancellationToken.None));

        doc.RootElement.TryGetProperty("shipTo", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("billTo", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("contact", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_EmitsNestedShipToBillToContact()
    {
        var svc = new JsonTransformService();
        var doc = await ReadJson(await svc.TransformAsync(BuildAddressedOrder(), OutputFormat.Json, CancellationToken.None));
        var root = doc.RootElement;

        var shipTo = root.GetProperty("shipTo");
        shipTo.GetProperty("name").GetString().Should().Be("Usine EXEMPLE de la REDACTED-PARTY");
        shipTo.GetProperty("deliverTo").GetString().Should().Be("Testperson Alex");
        shipTo.GetProperty("street").GetString().Should().Be("12 rue des Essais B12-3 (CTX_0000)");
        shipTo.GetProperty("city").GetString().Should().Be("VILLE-EXEMPLE");
        shipTo.GetProperty("postalCode").GetString().Should().Be("63040");
        shipTo.GetProperty("country").GetString().Should().Be("FRANCE");
        shipTo.GetProperty("email").GetString().Should().Be("ship@buyer.example.com");
        shipTo.GetProperty("phone").GetString().Should().Be("33100000000");

        var billTo = root.GetProperty("billTo");
        billTo.GetProperty("name").GetString().Should().Be("EXEMPLE Comptabilite Fournisseurs");
        billTo.GetProperty("street").GetString().Should().Be("Place des Essais Nord");
        billTo.GetProperty("postalCode").GetString().Should().Be("63000");

        var contact = root.GetProperty("contact");
        contact.GetProperty("name").GetString().Should().Be("Testperson Alex");
        contact.GetProperty("email").GetString().Should().Be("alex.testperson@buyer.example.com");
        contact.GetProperty("phone").GetString().Should().Be("33100000000");
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
                SupplierItemCode = null,
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = true,
                Confidence       = 0.5f,
            }
        };

        var svc = new JsonTransformService();
        var act = async () => await svc.TransformAsync(
            BuildOrder(lines: lines), OutputFormat.Json, CancellationToken.None);

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
                SupplierItemCode = null,
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        };

        var svc = new JsonTransformService();
        var act = async () => await svc.TransformAsync(
            BuildOrder(lines: lines), OutputFormat.Json, CancellationToken.None);

        await act.Should().ThrowAsync<TransformValidationException>();
    }

    [Fact]
    public async Task TransformAsync_ValidationException_ContainsUnresolvedLineNumbers()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, SupplierItemCode = "S-001",
                Quantity = 1m, UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, SupplierItemCode = null,
                Quantity = 1m, UnitPrice = 10m,
                NeedsReview = true, Confidence = 0.3f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 3, SupplierItemCode = null,
                Quantity = 1m, UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
        };

        var svc = new JsonTransformService();
        var ex  = await Assert.ThrowsAsync<TransformValidationException>(() =>
            svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Json, CancellationToken.None));

        ex.UnresolvedLineNumbers.Should().BeEquivalentTo(new[] { 2, 3 });
    }
}
