using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class X12TransformServiceTests
{
    private static PurchaseOrderEntity BuildOrder(
        string poNumber  = "PO-X12-001",
        string currency  = "USD",
        DateOnly? date   = null,
        IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = poNumber,
            OrderDate  = date ?? new DateOnly(2026, 5, 29),
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

    private static async Task<string> ReadContentAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8, leaveOpen: true);
        var s = await reader.ReadToEndAsync();
        result.Content.Position = 0;
        return s;
    }

    private static List<string> SplitSegments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries)
           .Select(s => s.Trim('\r', '\n', ' ', '\t'))
           .Where(s => s.Length > 0)
           .ToList();

    // ── Routing ────────────────────────────────────────────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForX12Only()
    {
        var svc = new X12TransformService();
        svc.CanTransform(OutputFormat.X12).Should().BeTrue();
        svc.CanTransform(OutputFormat.Ubl).Should().BeFalse();
        svc.CanTransform(OutputFormat.CXml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
        svc.CanTransform(OutputFormat.Json).Should().BeFalse();
    }

    // ── Envelope structure + content-type ──────────────────────────────────────

    [Fact]
    public async Task TransformAsync_EmitsWellFormed850Envelope()
    {
        var svc = new X12TransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None);

        result.ContentType.Should().Be("application/edi-x12");
        result.FileExtension.Should().Be(".x12");

        var edi  = await ReadContentAsString(result);
        var segs = SplitSegments(edi);

        // Envelope order: ISA / GS / ST / BEG / CUR / PO1 / CTT / SE / GE / IEA
        segs[0].Should().StartWith("ISA*");
        segs[1].Should().StartWith("GS*PO*");
        segs[2].Should().Be("ST*850*0001");
        segs[3].Should().StartWith("BEG*00*NE*PO-X12-001**20260529");
        segs.Should().Contain(s => s.StartsWith("CUR*BY*USD"));
        segs.Should().Contain(s => s.StartsWith("PO1*1*10*EA*125.00*PE*BP*BUYER-001*VP*SUP-ABC-001"));
        segs[^2].Should().Be("GE*1*1");
        segs[^1].Should().Be("IEA*1*000000001");

        // The fixed-width ISA must carry the component separator at ISA16 and the
        // canonical element separator right after "ISA".
        edi[3].Should().Be('*');
    }

    [Fact]
    public async Task TransformAsync_ComputesCorrectSeAndCttCounts()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity { LineNumber = 1, BuyerItemCode = "B1", SupplierItemCode = "S1", Description = "First",  Quantity = 1m, Unit = "EA", UnitPrice = 1m },
            new PurchaseOrderLineEntity { LineNumber = 2, BuyerItemCode = "B2", SupplierItemCode = "S2", Description = "Second", Quantity = 2m, Unit = "EA", UnitPrice = 2m },
        };

        var svc = new X12TransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);
        var segs = SplitSegments(await ReadContentAsString(result));

        // CTT = number of PO1 line items.
        segs.Should().Contain("CTT*2");

        // SE count = ST … SE inclusive. Transaction set segments here:
        //   ST, BEG, CUR, PO1, PID, PO1, PID, CTT, SE  → 9
        var se = segs.Single(s => s.StartsWith("SE*"));
        se.Should().Be("SE*9*0001");

        // SE control number must match ST.
        var st = segs.Single(s => s.StartsWith("ST*"));
        st.Should().EndWith("*0001");
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

        var svc = new X12TransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.UnresolvedLineNumbers.Should().ContainSingle().Which.Should().Be(1);
    }

    // ── Round-trip: transform → parse ───────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_RoundTripsThroughX12OrderParser()
    {
        var svc = new X12TransformService();
        var order = BuildOrder(
            poNumber: "PO-ROUNDTRIP-7",
            currency: "USD",
            date: new DateOnly(2026, 5, 29),
            lines: new[]
            {
                new PurchaseOrderLineEntity
                {
                    LineNumber       = 1,
                    BuyerItemCode    = "BUYER-RT-1",
                    SupplierItemCode = "SUP-RT-1",
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
                    BuyerItemCode    = "BUYER-RT-2",
                    SupplierItemCode = "SUP-RT-2",
                    Description      = "Round-trip bolt",
                    Quantity         = 250m,
                    Unit             = "EA",
                    UnitPrice        = 0.45m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                }
            });

        // Emit
        var result = await svc.TransformAsync(order, OutputFormat.X12, CancellationToken.None);

        // Feed the emitted bytes straight into the inbound parser
        result.Content.Position = 0;
        var parser = new X12OrderParser();
        var parsed = await parser.ParseAsync(result.Content, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-ROUNDTRIP-7");
        parsed.OrderDate.Should().Be(new DateTime(2026, 5, 29));
        parsed.Currency.Should().Be("USD");
        parsed.Lines.Should().HaveCount(2);

        var first = parsed.Lines.OrderBy(l => l.LineNumber).First();
        // The transformer emits BP=<buyerItemCode>, so the parser recovers it as the buyer item code.
        first.BuyerItemCode.Should().Be("BUYER-RT-1");
        first.Quantity.Should().Be(4m);
        first.UnitPrice.Should().Be(50.00m);
        first.Unit.Should().Be("EA");
        first.Description.Should().Be("Round-trip widget");

        var second = parsed.Lines.OrderBy(l => l.LineNumber).Last();
        second.BuyerItemCode.Should().Be("BUYER-RT-2");
        second.Quantity.Should().Be(250m);
        second.UnitPrice.Should().Be(0.45m);
    }
}
