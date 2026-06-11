using FluentAssertions;
using ProcuLink.Infrastructure.Services.Ai;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// P2 hardening — <c>ValidateAndMap</c> now reports a short per-line "why flagged"
/// reason alongside the flagged line numbers, so the persisted line can explain
/// itself in the review UI. Clean extractions report no reasons at all.
/// </summary>
public class OpenAiPdfOrderExtractorReviewReasonTests
{
    [Fact]
    public void ValidateAndMap_ArithmeticMismatch_ReportsAQtyTimesPriceReason()
    {
        // 4, 12.50 and 99.99 all appear in the source (no hallucination), but
        // 4 × 12.50 = 50.00 ≠ 99.99 → flagged with the arithmetic cause.
        const string source = "1 ABC Widget 4 PCS 12.50 99.99";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 99.99),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().Contain(1);
        result.ReviewReasons.Should().NotBeNull();
        result.ReviewReasons![1].Should().Contain("quantity × unit price does not match the stated line amount");
    }

    [Fact]
    public void ValidateAndMap_HallucinatedUnitPrice_ReportsANotInSourceReason()
    {
        // unit_price 13.50 never appears in the source; 4 × 13.50 = 54.00 reconciles,
        // so the ONLY cause is the hallucinated unit price.
        const string source = "1 ABC Widget 4 PCS 54.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 13.50, 54.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.ReviewReasons.Should().NotBeNull();
        result.ReviewReasons![1].Should().Contain("unit price does not appear in the source document");
        result.ReviewReasons[1].Should().NotContain("quantity × unit price", "arithmetic reconciles here");
    }

    [Fact]
    public void ValidateAndMap_CleanExtraction_ReportsNoReasons()
    {
        const string source = "PO-1 ABC Widget 4 PCS 12.50 50.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 50.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty();
        result.ReviewReasons.Should().BeNull();
    }
}
