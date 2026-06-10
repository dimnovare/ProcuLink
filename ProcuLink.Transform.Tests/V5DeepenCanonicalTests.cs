using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests;

/// <summary>
/// V5 deepen-canonical test suite.
///
/// Covers:
///   1. IDoc E1EDK03 IDDAT=012 → ParsedOrder.RequestedDeliveryDate
///   2. IDoc E1EDP20 EDATU     → ParsedOrderLine.DeliveryDate  (previously orphaned)
///   3. MappedTransformService.BuildHeaderRow exposes SubTotal, TaxTotal, GrandTotal,
///      PaymentTerms, RequestedDeliveryDate
///   4. MappedTransformService.BuildLineRow exposes LineAmount, TaxRate, DeliveryDate
///   5. ScribanOrderModel exposes RequestedDeliveryDate (header) and TaxRate/DeliveryDate (line)
///   6. Byte-identical guard: CSV and XML fixed transforms produce the SAME output
///      whether or not V5 fields are populated — they read entity typed columns only.
/// </summary>
public class V5DeepenCanonicalTests
{
    // ── IDoc fixture helpers ─────────────────────────────────────────────────────

    private static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(V5DeepenCanonicalTests).Assembly.Location)!,
        "Fixtures", "Idoc", name);

    private static Stream OpenFixture(string name) => File.OpenRead(FixturePath(name));

    // ── 1. RequestedDeliveryDate from IDoc E1EDK03 IDDAT=012 ────────────────────

    [Fact]
    public async Task IDoc11_ParseAsync_PopulatesRequestedDeliveryDate()
    {
        // idoc-orders05-11.xml carries E1EDK03 IDDAT=012 DATUM=20260525
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-11.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 5, 25),
            "E1EDK03 IDDAT=012 DATUM=20260525 is present in the fixture");
    }

    [Fact]
    public async Task IDoc710_ParseAsync_PopulatesRequestedDeliveryDate()
    {
        // idoc-orders05-710.xml also carries E1EDK03 IDDAT=012
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-710.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.RequestedDeliveryDate.Should().NotBeNull("idoc-orders05-710 carries E1EDK03 IDDAT=012");
    }

    [Fact]
    public async Task IDoc9_ParseAsync_PopulatesRequestedDeliveryDate()
    {
        // idoc-orders05-9.xml carries E1EDK03 IDDAT=012 DATUM=20260603
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-9.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 6, 3),
            "E1EDK03 IDDAT=012 DATUM=20260603 is present in idoc-orders05-9.xml");
    }

    // ── 2. Per-line DeliveryDate from IDoc E1EDP20 EDATU ──────────────────────

    [Fact]
    public async Task IDoc11_ParseAsync_PopulatesPerLineDeliveryDate()
    {
        // All 5 lines in idoc-orders05-11.xml carry E1EDP20 EDATU=20260528/20260529
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-11.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.Lines.Should().AllSatisfy(l =>
            l.DeliveryDate.Should().NotBeNull("every E1EDP01 in idoc-orders05-11 carries E1EDP20 EDATU"));

        // Line 5 (POSEX=00050) has EDATU=20260529 — verify it differs from the others
        var line5 = result.Lines.Single(l => l.LineNumber == 50);
        line5.DeliveryDate.Should().Be(new DateOnly(2026, 5, 29));

        // Lines 1-4 (POSEX=00010..00040) share EDATU=20260528
        result.Lines.Where(l => l.LineNumber < 50).Should()
            .AllSatisfy(l => l.DeliveryDate.Should().Be(new DateOnly(2026, 5, 28)));
    }

    [Fact]
    public async Task IDoc710_ParseAsync_PopulatesPerLineDeliveryDate()
    {
        var parser = new IDocOrders05Parser();
        await using var stream = OpenFixture("idoc-orders05-710.xml");

        var result = await parser.ParseAsync(stream, CancellationToken.None);

        result.Lines.Should().AllSatisfy(l =>
            l.DeliveryDate.Should().NotBeNull("every E1EDP01 in idoc-orders05-710 carries E1EDP20 EDATU"));
    }

    // ── 3. MappedTransformService.BuildHeaderRow — V5 fields ────────────────────

    [Fact]
    public void BuildHeaderRow_ExposesSubTotalTaxTotalGrandTotal_WhenStated()
    {
        var order = BuildOrder(subTotal: 100m, taxTotal: 20m, grandTotal: 120m,
                               paymentTerms: "NET30",
                               requestedDeliveryDate: new DateOnly(2026, 6, 15));
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildHeaderRow(order, ov);

        row["SubTotal"].Should().Be("100");
        row["TaxTotal"].Should().Be("20");
        row["GrandTotal"].Should().Be("120");
        row["PaymentTerms"].Should().Be("NET30");
        row["RequestedDeliveryDate"].Should().Be("2026-06-15");
    }

    [Fact]
    public void BuildHeaderRow_DerivesSubTotalAndGrandTotalWhenNull()
    {
        // SubTotal/GrandTotal are derived as sum(Qty*UnitPrice) when not stated
        var order = BuildOrder(subTotal: null, taxTotal: null, grandTotal: null);
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "A", SupplierItemCode = "S1",
                    Quantity = 3m, UnitPrice = 10m, NeedsReview = false },
        };
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildHeaderRow(order, ov);

        row["SubTotal"].Should().Be("30");   // 3 * 10
        row["GrandTotal"].Should().Be("30"); // same derivation
        row["TaxTotal"].Should().Be("0");    // defaults to 0 when null
    }

    [Fact]
    public void BuildHeaderRow_RequestedDeliveryDateIsEmptyWhenNull()
    {
        var order = BuildOrder();  // no requestedDeliveryDate
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildHeaderRow(order, ov);

        row["RequestedDeliveryDate"].Should().BeEmpty();
    }

    // ── 4. MappedTransformService.BuildLineRow — V5 fields ──────────────────────

    [Fact]
    public void BuildLineRow_ExposesLineAmountTaxRateAndDeliveryDate()
    {
        var order = BuildOrder();
        var line = new PurchaseOrderLineEntity
        {
            LineNumber       = 1,
            BuyerItemCode    = "B1",
            SupplierItemCode = "S1",
            Quantity         = 5m,
            UnitPrice        = 20m,
            LineAmount       = 100m,   // stated (= 5*20 for simplicity)
            TaxRate          = 0.25m,
            DeliveryDate     = new DateOnly(2026, 7, 1),
            NeedsReview      = false,
        };
        order.Lines = new List<PurchaseOrderLineEntity> { line };
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildLineRow(order, ov, line);

        row["LineAmount"].Should().Be("100");
        row["TaxRate"].Should().Be("0.25");
        row["DeliveryDate"].Should().Be("2026-07-01");
    }

    [Fact]
    public void BuildLineRow_LineAmountFallsBackToQtyTimesUnitPrice()
    {
        var order = BuildOrder();
        var line = new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
            Quantity = 4m, UnitPrice = 7.5m,
            LineAmount = null,  // not stated
            NeedsReview = false,
        };
        order.Lines = new List<PurchaseOrderLineEntity> { line };
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildLineRow(order, ov, line);

        row["LineAmount"].Should().Be("30.0");  // decimal.ToString(): 4m * 7.5m = 30.0m
    }

    [Fact]
    public void BuildLineRow_TaxRateAndDeliveryDateAreEmptyWhenNull()
    {
        var order = BuildOrder();
        var line = new PurchaseOrderLineEntity
        {
            LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
            Quantity = 1m, UnitPrice = 1m, NeedsReview = false,
        };
        order.Lines = new List<PurchaseOrderLineEntity> { line };
        var ov = new OrderMappingOverride();

        var row = MappedTransformService.BuildLineRow(order, ov, line);

        row["TaxRate"].Should().BeEmpty();
        row["DeliveryDate"].Should().BeEmpty();
    }

    // ── 5. ScribanOrderModel — V5 fields ─────────────────────────────────────────

    [Fact]
    public void ScribanOrderModel_ExposesRequestedDeliveryDate()
    {
        var order = BuildOrder(requestedDeliveryDate: new DateOnly(2026, 8, 10));
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
                    Quantity = 1m, UnitPrice = 1m, NeedsReview = false },
        };

        var root = ScribanOrderModel.Build(order, null);

        root["RequestedDeliveryDate"].Should().Be("2026-08-10");
    }

    [Fact]
    public void ScribanOrderModel_RequestedDeliveryDateEmptyWhenNull()
    {
        var order = BuildOrder();  // no requestedDeliveryDate
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
                    Quantity = 1m, UnitPrice = 1m, NeedsReview = false },
        };

        var root = ScribanOrderModel.Build(order, null);

        root["RequestedDeliveryDate"].Should().Be(string.Empty);
    }

    [Fact]
    public void ScribanOrderModel_ExposesPerLineTaxRateAndDeliveryDate()
    {
        var order = BuildOrder();
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
                    Quantity = 2m, UnitPrice = 10m, NeedsReview = false,
                    TaxRate = 0.20m, DeliveryDate = new DateOnly(2026, 9, 1) },
        };

        var root = ScribanOrderModel.Build(order, null);

        var lines = root["Lines"] as IEnumerable<object>;
        lines.Should().HaveCount(1);

        var line = (Scriban.Runtime.ScriptObject)lines!.Single();
        Convert.ToDecimal(line["TaxRate"]).Should().Be(0.20m);
        line["DeliveryDate"].Should().Be("2026-09-01");
    }

    [Fact]
    public void ScribanOrderModel_TaxRateAndDeliveryDateAreEmptyWhenNull()
    {
        var order = BuildOrder();
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S",
                    Quantity = 1m, UnitPrice = 1m, NeedsReview = false,
                    TaxRate = null, DeliveryDate = null },
        };

        var root = ScribanOrderModel.Build(order, null);

        var lines = root["Lines"] as IEnumerable<object>;
        var line = (Scriban.Runtime.ScriptObject)lines!.Single();
        line["TaxRate"].Should().Be(string.Empty);
        line["DeliveryDate"].Should().Be(string.Empty);
    }

    // ── 6. Byte-identical guard (CSV and XML — fully deterministic) ──────────────

    /// <summary>
    /// CSV and XML transforms produce the SAME byte-for-byte output regardless
    /// of whether V5 fields (RequestedDeliveryDate, TaxRate, DeliveryDate, etc.)
    /// are populated. These transforms read entity typed columns only and never
    /// touch the BuildHeaderRow/BuildLineRow row bags.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Xml)]
    public async Task FixedTransform_ByteIdenticalWhenV5FieldsAbsentOrPresent(OutputFormat format)
    {
        // Entity with no V5 fields set — all null.
        var orderWithout = ResolvedEntity();
        // Same structural entity WITH V5 fields set — the fixed transform must not read them.
        var orderWith = ResolvedEntity();
        orderWith.RequestedDeliveryDate = new DateOnly(2026, 12, 31);
        foreach (var l in orderWith.Lines)
        {
            l.TaxRate      = 0.23m;
            l.DeliveryDate = new DateOnly(2026, 12, 31);
            l.LineAmount   = l.Quantity * l.UnitPrice;
        }
        orderWith.SubTotal     = 1000m;
        orderWith.TaxTotal     = 230m;
        orderWith.GrandTotal   = 1230m;
        orderWith.PaymentTerms = "NET30";

        ITransformService svc = format == OutputFormat.Csv
            ? new CsvTransformService()
            : new XmlTransformService();

        var resultWithout = await svc.TransformAsync(orderWithout, format, CancellationToken.None);
        var resultWith    = await svc.TransformAsync(orderWith,    format, CancellationToken.None);

        var bytesWithout = await ReadBytesAsync(resultWithout);
        var bytesWith    = await ReadBytesAsync(resultWith);

        bytesWith.Should().Equal(bytesWithout,
            $"Fixed {format} transform must not change output when V5 fields are present " +
            "but no override/template references them");
    }

    /// <summary>
    /// For non-deterministic transforms (cXML has a fresh GUID per call, JSON has a UTC timestamp),
    /// verify that V5 field names do NOT appear anywhere in the output text.
    /// This proves the additive fields are invisible to fixed-format consumers.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.CXml)]
    [InlineData(OutputFormat.Json)]
    public async Task FixedTransform_V5FieldNamesAbsentInOutput(OutputFormat format)
    {
        var order = ResolvedEntity();
        order.RequestedDeliveryDate = new DateOnly(2026, 12, 31);
        foreach (var l in order.Lines)
        {
            l.TaxRate      = 0.23m;
            l.DeliveryDate = new DateOnly(2026, 12, 31);
            l.LineAmount   = l.Quantity * l.UnitPrice;
        }

        ITransformService svc = format == OutputFormat.CXml
            ? new CxmlTransformService()
            : new JsonTransformService();

        var result = await svc.TransformAsync(order, format, CancellationToken.None);
        var text   = Encoding.UTF8.GetString(await ReadBytesAsync(result));

        // The fixed transforms do not reference V5 field names in their serialization.
        // These field names would only appear if the transform were accidentally reading the row bag.
        text.Should().NotContain("RequestedDeliveryDate",
            $"Fixed {format} transform must not emit the V5 RequestedDeliveryDate field");
        text.Should().NotContain("TaxRate",
            $"Fixed {format} transform must not emit the V5 TaxRate field");

        // DeliveryDate: check only that the specific ISO value "2026-12-31" does not appear.
        // Neither CxmlTransformService nor JsonTransformService write a delivery-date field.
        text.Should().NotContain("2026-12-31",
            $"Fixed {format} transform must not bleed the V5 DeliveryDate value into output");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static PurchaseOrderEntity BuildOrder(
        decimal?  subTotal              = null,
        decimal?  taxTotal              = null,
        decimal?  grandTotal            = null,
        string?   paymentTerms          = null,
        DateOnly? requestedDeliveryDate = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id                    = Guid.NewGuid(),
            OrgId                 = Guid.NewGuid(),
            SupplierId            = Guid.NewGuid(),
            PoNumber              = "PO-V5-001",
            BuyerName             = "V5 Buyer Ltd",
            OrderDate             = new DateOnly(2026, 6, 10),
            Currency              = "EUR",
            Status                = "ready",
            SubTotal              = subTotal,
            TaxTotal              = taxTotal,
            GrandTotal            = grandTotal,
            PaymentTerms          = paymentTerms,
            RequestedDeliveryDate = requestedDeliveryDate,
        };
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber       = 1,
                BuyerItemCode    = "BV-1",
                SupplierItemCode = "SV-1",
                Description      = "V5 Widget",
                Quantity         = 2m,
                Unit             = "EA",
                UnitPrice        = 50m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            },
        };
        return order;
    }

    /// <summary>Fixed-format entity (resolved supplier item codes; NeedsReview=false).</summary>
    private static PurchaseOrderEntity ResolvedEntity()
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-BI-001",
            BuyerName  = "BI Buyer OY",
            OrderDate  = new DateOnly(2026, 5, 15),
            Currency   = "EUR",
            Status     = "ready",
            Supplier   = new Supplier { Name = "BI Supplier AB" },
        };
        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, BuyerItemCode = "B1", SupplierItemCode = "S1",
                    Description = "Part Alpha", Quantity = 4m, Unit = "EA", UnitPrice = 25m,
                    NeedsReview = false, Confidence = 1.0f },
            new() { LineNumber = 2, BuyerItemCode = "B2", SupplierItemCode = "S2",
                    Description = "Part Beta",  Quantity = 1m, Unit = "EA", UnitPrice = 100m,
                    NeedsReview = false, Confidence = 1.0f },
        };
        return order;
    }

    private static async Task<byte[]> ReadBytesAsync(TransformResult r)
    {
        r.Content.Position = 0;
        using var ms = new MemoryStream();
        await r.Content.CopyToAsync(ms);
        return ms.ToArray();
    }
}
