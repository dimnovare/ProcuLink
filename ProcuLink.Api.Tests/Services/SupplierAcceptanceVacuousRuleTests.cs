using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// A rule the customer BUYS must be able to fail, or it must say it did not run.
///
/// <para><c>line_amount_reconcile</c> is sold from the catalog as "Reject lines where the printed
/// line amount diverges from quantity × unit price beyond tolerance". Its evaluator read
/// <c>stated = l.LineAmount ?? computed</c>, so when no line amount had been parsed it compared
/// <c>computed</c> against itself — an identity, not a comparison. Nine of the eleven line-producing
/// parsers never populate <c>LineAmount</c>, so for a CSV, XLSX, UBL, EDIFACT, X12,
/// deterministic-PDF or email-body order the rule was arithmetically incapable of rejecting
/// anything, while the UI reported it green.</para>
///
/// <para><b>These tests drive a REAL deterministic parser rather than hand-building an entity.</b>
/// That is deliberate: a fixture that sets <c>LineAmount</c> itself passes without exercising the
/// defect at all, and the premise under test — that the parser really does drop the printed
/// amount — is exactly the thing a hand-built fixture would assume instead of proving.</para>
/// </summary>
public class SupplierAcceptanceVacuousRuleTests
{
    private static SupplierAcceptanceProfile Profile(params SupplierAcceptanceRule[] rules) =>
        new() { Id = Guid.NewGuid(), Rules = new(rules) };

    private static SupplierAcceptanceRule Rule(
        string scope, string field, string op, string? expected = null, string severity = "warning") =>
        new() { Id = Guid.NewGuid(), Scope = scope, FieldPath = field, Operator = op, ExpectedValue = expected, Severity = severity };

    /// <summary>
    /// A CSV whose document PRINTS a line total of 99.00 on a line whose quantity × unit price is
    /// 10.00 — a genuine divergence a buyer would want rejected. <c>CsvOrderParser</c> has no header
    /// alias for a line-total column at all (<c>RawRowMap</c>), so the printed figure is dropped and
    /// the line reaches the database with <c>LineAmount = null</c>.
    /// </summary>
    private const string DivergingCsv =
        "PoNumber,LineNumber,Sku,Description,Quantity,Price,LineTotal\r\n" +
        "PO-VAC-1,1,ACM-BLT-10,Hex bolt M10,2,5.00,99.00\r\n";

    private const decimal PrintedLineTotal = 99.00m;

    private static async Task<IReadOnlyList<PurchaseOrderLineEntity>> ParseCsvToLinesAsync(string csv)
    {
        var parser = new CsvOrderParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        // Mirrors the persistence mapping in OrderIngestionService.cs (LineAmount = line.LineAmount,
        // DeliveryDate = line.DeliveryDate) — nothing here invents a value the parser did not produce.
        return parsed.Lines.Select(l => new PurchaseOrderLineEntity
        {
            LineNumber       = l.LineNumber,
            BuyerItemCode    = l.BuyerItemCode,
            Description      = l.Description,
            Quantity         = l.Quantity,
            UnitPrice        = l.UnitPrice ?? 0m,
            LineAmount       = l.LineAmount,
            DeliveryDate     = l.DeliveryDate,
        }).ToList();
    }

    /// <summary>
    /// The premise, asserted rather than assumed: the deterministic parser really does drop the
    /// printed line total, and the divergence in the fixture is real. If this ever goes green for
    /// the wrong reason — CSV gaining a line-amount alias, or the fixture losing its divergence —
    /// the control below would stop testing anything, so it is checked separately and first.
    /// </summary>
    [Fact]
    public async Task Premise_DeterministicCsvParser_DropsThePrintedLineAmount_AndTheFixtureReallyDiverges()
    {
        var lines = await ParseCsvToLinesAsync(DivergingCsv);

        lines.Should().ContainSingle("the fixture is one line");
        var line = lines[0];

        line.LineAmount.Should().BeNull(
            "CsvOrderParser's RawRowMap has no line-total alias — the printed 99.00 is dropped");
        (line.Quantity * line.UnitPrice).Should().Be(
            10.00m, "2 × 5.00 — this is what the rule would compute");
        PrintedLineTotal.Should().NotBe(
            line.Quantity * line.UnitPrice,
            "the document's printed total genuinely disagrees with qty × price, so a working rule has something to catch");
    }

    /// <summary>
    /// THE CONTROL FOR THE DEFECT. An order from a deterministic parser, whose document printed a
    /// diverging line amount, must not report the reconcile rule as passing. It cannot report a
    /// failure either — the divergence was lost at parse time and the evaluator never saw it — so
    /// the honest answer, and the one this asserts, is not-evaluated.
    /// </summary>
    [Fact]
    public async Task LineAmountReconcile_OnDeterministicParserOrder_DoesNotReportAPass()
    {
        var lines = await ParseCsvToLinesAsync(DivergingCsv);
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        foreach (var l in lines) order.Lines.Add(l);

        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id,
            Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")),
            order, DateTime.UtcNow);

        results.Should().ContainSingle();
        results[0].Status.Should().NotBe(
            OrderValidationResult.StatusPass,
            "the rule examined nothing — the printed amount never reached the evaluator");
        results[0].Status.Should().Be(OrderValidationResult.StatusNotEvaluated);
    }

    /// <summary>
    /// The negative control for the test above: with a printed amount actually present, the rule
    /// still fails on a divergence. Without this, "never reports a pass" would also be satisfied by
    /// a rule that had been broken into never running at all.
    /// </summary>
    [Fact]
    public void LineAmountReconcile_StillFails_WhenAPrintedAmountIsPresentAndDiverges()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity
            {
                LineNumber = 1, Quantity = 2m, UnitPrice = 5m, LineAmount = PrintedLineTotal,
            } },
        };

        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id,
            Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")),
            order, DateTime.UtcNow);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(
            OrderValidationResult.StatusFail,
            "2 × 5 = 10 against a printed 99 — the rule must still catch this");
    }

    /// <summary>And it still passes when the printed amount agrees — the rule is not simply inert.</summary>
    [Fact]
    public void LineAmountReconcile_StillPasses_WhenThePrintedAmountAgrees()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity
            {
                LineNumber = 1, Quantity = 2m, UnitPrice = 5m, LineAmount = 10.00m,
            } },
        };

        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id,
            Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")),
            order, DateTime.UtcNow);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(OrderValidationResult.StatusPass);
    }

    /// <summary>
    /// A not-evaluated rule must never start REFUSING orders. Absence was non-blocking before this
    /// change (it reported a pass) and must stay non-blocking now that it reports honestly —
    /// otherwise the fix would begin rejecting every CSV order carrying this rule.
    /// </summary>
    [Fact]
    public async Task NotEvaluated_NeverBlocks_EvenWhenTheRuleIsAnErrorSeverityBlocker()
    {
        var lines = await ParseCsvToLinesAsync(DivergingCsv);
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        foreach (var l in lines) order.Lines.Add(l);

        var blocking = Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01", severity: "error");
        blocking.BlockOnFail = true;

        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(blocking), order, DateTime.UtcNow);

        results[0].Status.Should().Be(OrderValidationResult.StatusNotEvaluated);
        results.Should().NotContain(
            r => r.Status == OrderValidationResult.StatusFail,
            "GetBlockingFailuresAsync collects only failures — a rule that could not run must not refuse the order");
    }

    // ── Unresolved field paths ────────────────────────────────────────────────

    /// <summary>
    /// A rule whose field path the evaluator does not implement examined nothing, so it reports
    /// not-evaluated. It previously resolved to a null value, which the absence-tolerant operators
    /// then waved through as a pass — so a typo'd or unsupported path rendered green.
    /// </summary>
    [Theory]
    [InlineData("order", "noSuchOrderField", "date_sanity")]
    [InlineData("order", "noSuchOrderField", "not_label")]
    [InlineData("order", "noSuchOrderField", "vat_format")]
    [InlineData("line", "noSuchLineField", "date_sanity")]
    [InlineData("line", "noSuchLineField", "not_label")]
    [InlineData("line", "noSuchLineField", "vat_format")]
    public void UnresolvedFieldPath_IsNotEvaluated_NotAPass(string scope, string fieldPath, string op)
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = 1m } },
        };
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "Teststadt", Vat = "ATU99000000", Country = "AT" });

        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule(scope, fieldPath, op)), order, DateTime.UtcNow);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(
            OrderValidationResult.StatusNotEvaluated,
            "'{0}' is not a field path this evaluator resolves, so nothing was examined", fieldPath);
    }

    /// <summary>
    /// The negative control for the theory above: a KNOWN field path with the same operators is
    /// still really evaluated. Without it, "unresolved paths are not-evaluated" would also hold for
    /// an evaluator that had stopped resolving anything at all.
    /// </summary>
    [Fact]
    public void KnownFieldPath_WithTheSameOperators_IsStillEvaluated()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "Teststadt", Vat = "ATU99000000", Country = "AT" });

        var notLabel = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToCity", "not_label", expected: "City,UIDNr")), order, DateTime.UtcNow);
        var vatFormat = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);

        notLabel[0].Status.Should().Be(OrderValidationResult.StatusPass, "'Teststadt' is a real city");
        vatFormat[0].Status.Should().Be(OrderValidationResult.StatusPass, "'ATU99000000' is a well-formed Austrian VAT");
    }
}
