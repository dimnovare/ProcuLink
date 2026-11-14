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

    // ── 6. Byte-identical guard (CSV and XML — fully deterministic) ──────────────

    /// <summary>
    /// CSV and XML transforms produce the SAME byte-for-byte output regardless
    /// of whether V5 fields (RequestedDeliveryDate, TaxRate, DeliveryDate, etc.)
    /// are populated. These transforms read entity typed columns only.
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
