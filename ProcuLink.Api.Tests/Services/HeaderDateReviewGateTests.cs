using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The order-level half of the date-ambiguity guard. A parser that reports a HEADER field it
/// could not read unambiguously (today: an order date whose day/month ordering was a genuine
/// ≤12/≤12 coin-flip) must actually stop the order — otherwise the flag is decoration.
///
/// <para>The line scan alone cannot carry this: a header field has no per-line home, and
/// <c>Any()</c> over an EMPTY line set is <b>false</b> (OrderStatusMachine.cs:223), so a
/// header-only problem on a lineless parse would sail through as <c>ready</c>.</para>
/// </summary>
public class HeaderDateReviewGateTests
{
    private static PurchaseOrderLineEntity Line(bool needsReview) =>
        new() { LineNumber = 1, BuyerItemCode = "BUY-1", NeedsReview = needsReview };

    private static ParsedOrder Order(bool needsReview) =>
        new(
            PoNumber:   "PO-1",
            OrderDate:  new DateTime(2026, 4, 3),
            BuyerName:  "Acme",
            Currency:   "EUR",
            Lines:      System.Array.Empty<ParsedOrderLine>(),
            NeedsReview: needsReview,
            ReviewReason: needsReview ? "The order date \"03/04/2026\" could be day-first or month-first." : null);

    [Fact]
    public void Clean_lines_and_clean_header_does_not_need_review()
        => OrderIngestionService.OrderNeedsReview(new[] { Line(false) }, Order(false))
            .Should().BeFalse("nothing was flagged — the order must be free to reach 'ready'");

    [Fact]
    public void A_flagged_line_still_needs_review()
        => OrderIngestionService.OrderNeedsReview(new[] { Line(true) }, Order(false))
            .Should().BeTrue("the pre-existing line term must keep working");

    [Fact]
    public void An_ambiguous_header_date_needs_review_even_when_every_line_is_clean()
        => OrderIngestionService.OrderNeedsReview(new[] { Line(false), Line(false) }, Order(true))
            .Should().BeTrue(
                "the date the whole order ships under was a coin-flip; clean lines do not make " +
                "it readable");

    [Fact]
    public void An_ambiguous_header_date_needs_review_when_there_are_NO_lines()
        => OrderIngestionService.OrderNeedsReview(System.Array.Empty<PurchaseOrderLineEntity>(), Order(true))
            .Should().BeTrue(
                "Any() over an empty set is false — this is exactly the case a line-only scan " +
                "misses (OrderStatusMachine.cs:223)");

    /// <summary>
    /// The gate only has teeth because pending_review cannot start a transform. If
    /// <c>TransformableFrom</c> ever admits it, flagging the order stops blocking delivery and
    /// this whole guard becomes decoration — so pin the property the guard depends on.
    /// </summary>
    [Fact]
    public void PendingReview_cannot_start_a_transform()
    {
        OrderStatusMachine.TransformableFrom.Should().NotContain(OrderStatusConstants.PendingReview,
            "an order held for human review must not be transformable — that is what makes the " +
            "header-date flag block delivery rather than merely annotate the order");
        OrderStatusMachine.TransformableFrom.Should().Contain(OrderStatusConstants.Ready,
            "guard against a vacuous pass: the set must still admit the normal path");
    }
}
