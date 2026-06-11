using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// P2 hardening — the per-line "why was this flagged" reason written at parse time.
/// Covers the three flag origins: unresolved supplier code, parser numeric ambiguity
/// (CSV locale guard), and the structured-extraction overlay (anti-hallucination +
/// scanned-PDF vision reasons threaded through <c>ApplyExtractionReviewFlags</c>).
/// </summary>
public class LineReviewReasonTests
{
    private static ParsedOrderLine Line(
        string buyerCode = "BUY-1", bool needsReview = false, string? reviewReason = null) =>
        new(1, buyerCode, "Widget", 5m, "PCS", 10m, NeedsReview: needsReview, ReviewReason: reviewReason);

    // ── ComposeReviewReason (entity creation in BuildLineEntitiesAsync) ───────

    [Fact]
    public void ComposeReviewReason_ResolvedAndClean_IsNull()
        => OrderIngestionService.ComposeReviewReason(resolved: true, parserFlagged: false, Line())
            .Should().BeNull();

    [Fact]
    public void ComposeReviewReason_UnresolvedCode_NamesTheBuyerCode()
    {
        var reason = OrderIngestionService.ComposeReviewReason(
            resolved: false, parserFlagged: false, Line(buyerCode: "ACME-42"));

        reason.Should().Be("No supplier item code mapping was found for buyer code 'ACME-42'.");
    }

    [Fact]
    public void ComposeReviewReason_BlankBuyerCode_ExplainsThereIsNothingToResolve()
    {
        var reason = OrderIngestionService.ComposeReviewReason(
            resolved: false, parserFlagged: false, Line(buyerCode: "  "));

        reason.Should().Be("The line has no buyer item code to resolve against supplier mappings.");
    }

    [Fact]
    public void ComposeReviewReason_ParserFlagged_UsesTheParsersOwnReason()
    {
        var reason = OrderIngestionService.ComposeReviewReason(
            resolved: true, parserFlagged: true,
            Line(needsReview: true,
                 reviewReason: "The quantity could not be read unambiguously from the source file."));

        reason.Should().Be("The quantity could not be read unambiguously from the source file.");
    }

    [Fact]
    public void ComposeReviewReason_ParserFlaggedWithoutSpecificReason_FallsBackToGeneric()
    {
        var reason = OrderIngestionService.ComposeReviewReason(
            resolved: true, parserFlagged: true, Line(needsReview: true));

        reason.Should().Be("A quantity or unit price could not be read unambiguously from the source file.");
    }

    [Fact]
    public void ComposeReviewReason_UnresolvedAndParserFlagged_CombinesBothReasons()
    {
        var reason = OrderIngestionService.ComposeReviewReason(
            resolved: false, parserFlagged: true,
            Line(buyerCode: "ACME-42", needsReview: true,
                 reviewReason: "The unit price could not be read unambiguously from the source file."));

        reason.Should().Be(
            "No supplier item code mapping was found for buyer code 'ACME-42'. " +
            "The unit price could not be read unambiguously from the source file.");
    }

    // ── CSV parser origin (locale-ambiguity guard) ────────────────────────────

    [Fact]
    public async Task CsvParser_AmbiguousQuantity_WritesAReviewReasonOnTheParsedLine()
    {
        // Scientific-notation quantity — the locale guard refuses to guess.
        const string csv = "po_number,line_no,buyer_code,description,qty,unit_price\n" +
                           "PO-1,1,BUY-1,Widget,1.5e2,10.00\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var parsed = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        var line = parsed.Lines.Should().ContainSingle().Subject;
        line.NeedsReview.Should().BeTrue();
        line.ReviewReason.Should().Be("The quantity could not be read unambiguously from the source file.");
    }

    [Fact]
    public async Task CsvParser_CleanNumbers_LeavesReviewReasonNull()
    {
        const string csv = "po_number,line_no,buyer_code,description,qty,unit_price\n" +
                           "PO-1,1,BUY-1,Widget,5,10.00\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        var parsed = await new CsvOrderParser().ParseAsync(stream, CancellationToken.None);

        var line = parsed.Lines.Should().ContainSingle().Subject;
        line.NeedsReview.Should().BeFalse();
        line.ReviewReason.Should().BeNull();
    }

    // ── Structured-extraction overlay (anti-hallucination / scanned-PDF vision) ──

    [Fact]
    public void ApplyExtractionReviewFlags_WritesThePerLineExtractionReason()
    {
        var lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, SupplierItemCode = "SUP-1", NeedsReview = false, Confidence = 1.0f },
            new() { LineNumber = 2, SupplierItemCode = "SUP-2", NeedsReview = false, Confidence = 1.0f },
        };
        var reasons = new Dictionary<int, string>
        {
            [2] = "AI extraction flagged this line: quantity × unit price does not match the stated line amount.",
        };

        OrderService.ApplyExtractionReviewFlags(lines, new[] { 2 }, reasons);

        lines[0].NeedsReview.Should().BeFalse();
        lines[0].ReviewReason.Should().BeNull();
        lines[1].NeedsReview.Should().BeTrue();
        lines[1].ReviewReason.Should().Be(reasons[2]);
    }

    [Fact]
    public void ApplyExtractionReviewFlags_NoReasonForLine_FallsBackToGenericExtractionReason()
    {
        var lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, NeedsReview = false, Confidence = 1.0f },
        };

        OrderService.ApplyExtractionReviewFlags(lines, new[] { 1 }, reviewReasons: null);

        lines[0].NeedsReview.Should().BeTrue();
        lines[0].ReviewReason.Should().Be(
            "AI extraction flagged this line: a number could not be verified against the source document.");
    }

    [Fact]
    public void ApplyExtractionReviewFlags_AppendsAfterAnExistingParseTimeReason()
    {
        var lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber = 1, NeedsReview = true, Confidence = 0f,
                ReviewReason = "No supplier item code mapping was found for buyer code 'BUY-1'.",
            },
        };
        var reasons = new Dictionary<int, string>
        {
            [1] = "AI extraction flagged this line: the extracted quantity does not appear in the source document.",
        };

        OrderService.ApplyExtractionReviewFlags(lines, new[] { 1 }, reasons);

        lines[0].ReviewReason.Should().Be(
            "No supplier item code mapping was found for buyer code 'BUY-1'. " +
            "AI extraction flagged this line: the extracted quantity does not appear in the source document.");
    }
}
