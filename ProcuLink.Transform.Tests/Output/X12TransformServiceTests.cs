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

    [Fact]
    public async Task ContactPresentButNoPartyNames_DoesNotEmitOrphanPer()
    {
        // A PER in the 850 heading is only valid INSIDE an N1 loop. An order with a contact but no
        // ship-to / bill-to / buyer NAME has no N1 loop, so the contact PER must be suppressed (else a
        // strict 850 validator rejects an orphan heading PER).
        var order = BuildOrder();
        order.BuyerName    = "";                  // → ExtractBuyerName empty → no N1*BY
        order.ContactName  = "REDACTED-NAME";
        order.ContactEmail = "redacted@example.invalid";
        order.ContactPhone = "REDACTED-PHONE";
        // no ShipToName / BillToName → no N1*ST / N1*BT

        var result = await new X12TransformService().TransformAsync(order, OutputFormat.X12, CancellationToken.None);
        var segs = SplitSegments(await ReadContentAsString(result));

        segs.Any(s => s.StartsWith("N1")).Should().BeFalse("no party name → no N1 loop");
        segs.Any(s => s.StartsWith("PER")).Should().BeFalse("a heading PER is only valid inside an N1 loop");
    }

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

        // WP-20: the envelope now comes from the one DeliveryMediaTypes table, so the type this
        // transform STORES is the same string delivery puts on the wire. "application/EDI-X12" is
        // the IANA registration; media types compare case-insensitively, so a receiver matching the
        // lowercase spelling still matches.
        result.ContentType.Should().Be("application/EDI-X12");
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

    // ── Address (N1 loop) + contact (PER) + buyer party ─────────────────────────

    private static PurchaseOrderEntity BuildAddressedOrder()
    {
        var order = BuildOrder();
        order.BuyerName        = "REDACTED-PARTY";
        order.ContactName      = "REDACTED-NAME";
        order.ContactEmail     = "redacted@example.invalid";
        order.ContactPhone     = "REDACTED-PHONE";
        order.ShipToName       = "REDACTED-PARTY";
        order.ShipToDeliverTo  = "REDACTED-NAME";
        order.ShipToStreet     = "REDACTED-ADDRESS";
        order.ShipToCity       = "REDACTED-ADDRESS";
        order.ShipToPostalCode = "63040";
        order.ShipToCountry    = "FR";
        order.ShipToEmail      = "redacted@example.invalid";
        order.ShipToPhone      = "REDACTED-PHONE";
        order.BillToName       = "REDACTED-PARTY";
        order.BillToStreet     = "REDACTED-ADDRESS";
        order.BillToCity       = "REDACTED-ADDRESS";
        order.BillToPostalCode = "63000";
        order.BillToCountry    = "FR";
        return order;
    }

    [Fact]
    public async Task TransformAsync_NoAddressData_EmitsNoN1Segments_AndCountsUnchanged()
    {
        // BYTE-SAFETY LOCK: a 2-line order with NO address/contact/buyer-name data must emit zero
        // N1/N3/N4/PER segments, so SE/CTT match the pre-feature baseline exactly (SE*9 for 2 lines
        // with PID — same as TransformAsync_ComputesCorrectSeAndCttCounts).
        var lines = new[]
        {
            new PurchaseOrderLineEntity { LineNumber = 1, BuyerItemCode = "B1", SupplierItemCode = "S1", Description = "First",  Quantity = 1m, Unit = "EA", UnitPrice = 1m },
            new PurchaseOrderLineEntity { LineNumber = 2, BuyerItemCode = "B2", SupplierItemCode = "S2", Description = "Second", Quantity = 2m, Unit = "EA", UnitPrice = 2m },
        };

        var svc  = new X12TransformService();
        var segs = SplitSegments(await ReadContentAsString(
            await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None)));

        segs.Should().NotContain(s => s.StartsWith("N1*"));
        segs.Should().NotContain(s => s.StartsWith("PER*"));
        segs.Should().Contain("CTT*2");
        segs.Single(s => s.StartsWith("SE*")).Should().Be("SE*9*0001");
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_EmitsN1Loop_BetweenCurAndPo1()
    {
        var svc  = new X12TransformService();
        var edi  = await ReadContentAsString(
            await svc.TransformAsync(BuildAddressedOrder(), OutputFormat.X12, CancellationToken.None));
        var segs = SplitSegments(edi);

        // Ship-to N1*ST + N3 (street) + N4 (city**postal*country) + PER*OC (ship contact).
        segs.Should().Contain(s => s.StartsWith("REDACTED-PARTY"));
        segs.Should().Contain(s => s.StartsWith("REDACTED-ADDRESS"));
        segs.Should().Contain("REDACTED-ADDRESS");
        segs.Should().Contain(s => s.StartsWith("REDACTED-TEST-DATA"));

        // Bill-to N1*BT + N3 + N4 (no bill contact → no PER for BT).
        segs.Should().Contain(s => s.StartsWith("REDACTED-PARTY"));
        segs.Should().Contain("REDACTED-ADDRESS");

        // Buyer N1*BY, name-only — no N3/N4 immediately after a BY (buyer has no postal address).
        segs.Should().Contain(s => s.StartsWith("REDACTED-PARTY"));

        // Order-level contact PER*BD from Contact*.
        segs.Should().Contain(s => s.StartsWith("REDACTED-TEST-DATA"));

        // The N1 loop sits AFTER CUR and BEFORE the first PO1.
        var curIdx = edi.IndexOf("CUR*", StringComparison.Ordinal);
        var n1Idx  = edi.IndexOf("N1*",  StringComparison.Ordinal);
        var po1Idx = edi.IndexOf("PO1*", StringComparison.Ordinal);
        curIdx.Should().BeGreaterThanOrEqualTo(0);
        n1Idx.Should().BeGreaterThan(curIdx);
        po1Idx.Should().BeGreaterThan(n1Idx);
    }

    [Fact]
    public async Task TransformAsync_AddressFreeText_IsSanitizedOfDelimiters()
    {
        // A delimiter in a free-text address field has no X12 escape; it must be space-substituted
        // (Sanitize), never allowed to corrupt the segment structure.
        var order = BuildOrder();
        order.ShipToName   = "ACME*Logistics>Hub";
        order.ShipToStreet = "1 Main~Road";

        var svc  = new X12TransformService();
        var edi  = await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.X12, CancellationToken.None));
        var segs = SplitSegments(edi);

        // No raw delimiter leaked into the N1/N3 content.
        segs.Should().Contain(s => s.StartsWith("N1*ST*ACME Logistics Hub"));
        segs.Should().Contain(s => s.StartsWith("N3*1 Main Road"));
    }

    // ── Required-field + delimiter-in-code hardening ────────────────────────────

    [Fact]
    public async Task TransformAsync_DelimiterInSupplierItemCode_IsFlaggedNotSilentlyCorrupted()
    {
        // "ABC*123>REVB" is a structured part code; space-substituting the delimiters
        // would corrupt the vendor part hierarchy. X12 has no escape, so the line is
        // held for review rather than delivered mangled.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "ABC*123>REVB",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 5m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new X12TransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.Sanitized);
    }

    [Fact]
    public async Task TransformAsync_EmptyBuyerItemCode_IsRejected()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 5m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new X12TransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.MissingItemCode);
    }

    [Fact]
    public async Task TransformAsync_ZeroUnitPrice_NowTransforms()
    {
        // €0 is a legitimately-free line (founder-approved): it transforms, not held.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 0m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new X12TransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);

        result.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task TransformAsync_NegativeUnitPrice_IsFlaggedForReview()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = -5m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new X12TransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.X12, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.MissingOrZeroPrice);
    }

    [Fact]
    public async Task TransformAsync_CleanSupplierCode_StillSerializesUnchanged()
    {
        // A clean supplier code with no delimiters must serialize exactly as before —
        // the guard is a no-op for valid data.
        var svc = new X12TransformService();
        var result = await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None);
        var segs = SplitSegments(await ReadContentAsString(result));

        segs.Should().Contain(s => s.StartsWith("PO1*1*10*EA*125.00*PE*BP*BUYER-001*VP*SUP-ABC-001"));
    }
}
