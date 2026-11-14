using FluentAssertions;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// P2 hardening — the per-line "why was this flagged" reason, at its parser origin (the CSV
/// locale guard). (2026-11-13, plan 13b: the other two origins this covered —
/// <c>OrderIngestionService.ComposeReviewReason</c> and <c>OrderService.ApplyExtractionReviewFlags</c>
/// — went with the order pipeline.)
/// </summary>
public class LineReviewReasonTests
{
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
}
