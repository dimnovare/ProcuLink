using FluentAssertions;
using ProcuLink.Infrastructure.Services.Ai;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// The Gjensidige bug: a real line had unit price 3337.08 (ex-VAT) but a line total of
/// 4171.35 (= 3337.08 × 1.25, with 25 % Norwegian VAT). The plain
/// <c>quantity × unitPrice ≈ lineAmount</c> reconcile would flag that as a mismatch
/// (834.27 off) even though it is perfectly correct — an ex-VAT unit price against a
/// VAT-inclusive line total. These tests pin the VAT-aware reconcile:
///
/// <list type="bullet">
///   <item>A line whose total is explained by the captured tax rate is NOT flagged.</item>
///   <item>A line that still does not reconcile (no tax explains the gap) IS flagged
///   with the plain arithmetic reason.</item>
///   <item>When NO tax is captured the original ex-VAT reconcile is unchanged (no
///   over-flagging of the historical happy path).</item>
/// </list>
/// </summary>
public class OpenAiPdfOrderExtractorTaxReconcileTests
{
    [Fact]
    public void ValidateAndMap_VatInclusiveLineTotal_WithCapturedTaxRate_IsNotFlagged()
    {
        // 1 × 3337.08 = 3337.08; with tax_rate 25 → 3337.08 × 1.25 = 4171.35 (the printed total).
        // All three numbers appear in the source (no hallucination). The line is CORRECT.
        const string source = "1 GJ Service 1 EA 3337.08 25 4171.35";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "REDACTED-ITEM", OrderDate: "", Currency: "NOK", BuyerName: "Gjensidige Forsikring",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "GJ", "Service", 1, "EA", 3337.08, 4171.35, TaxRate: 25.0),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty("the VAT-inclusive total is explained by the 25% tax rate");
        result.ReviewReasons.Should().BeNull();
        // The captured tax rate must survive onto the line.
        result.Order!.Lines.Single().TaxRate.Should().Be(25.0m);
    }

    [Fact]
    public void ValidateAndMap_VatInclusiveLineTotal_WithCapturedTaxAmount_IsNotFlagged()
    {
        // tax_amount 834.27 (= 4171.35 − 3337.08) explains the gap even with no tax_rate.
        const string source = "1 GJ Service 1 EA 3337.08 834.27 4171.35";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "REDACTED-ITEM", OrderDate: "", Currency: "NOK", BuyerName: "Gjensidige Forsikring",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "GJ", "Service", 1, "EA", 3337.08, 4171.35, TaxRate: null, TaxAmount: 834.27),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().BeEmpty("the printed tax amount explains the VAT-inclusive total");
        result.Order!.Lines.Single().TaxAmount.Should().Be(834.27m);
    }

    [Fact]
    public void ValidateAndMap_GenuineMismatch_NoTaxExplainsTheGap_IsStillFlagged()
    {
        // 1 × 3337.08 = 3337.08; tax_rate 10 → 3670.79, but the total is 4171.35 → still off.
        // The gap is NOT explained by tax → genuine mismatch → flagged.
        const string source = "1 GJ Service 1 EA 3337.08 10 4171.35";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "REDACTED-ITEM", OrderDate: "", Currency: "NOK", BuyerName: "Gjensidige Forsikring",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(
                    1, "GJ", "Service", 1, "EA", 3337.08, 4171.35, TaxRate: 10.0),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.ReviewLineNumbers.Should().Contain(1);
        result.ReviewReasons.Should().NotBeNull();
        result.ReviewReasons![1].Should().Contain("quantity × unit price does not match the stated line amount");
    }

    [Fact]
    public void ValidateAndMap_NoTaxCaptured_ExVatReconcileUnchanged()
    {
        // No tax at all: 4 × 12.50 = 50.00 matches the printed line amount → not flagged,
        // exactly as before the tax-aware change (no over-flagging of the historical path).
        const string source = "1 ABC Widget 4 PCS 12.50 50.00";

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

    [Fact]
    public void ValidateAndMap_NoTaxCaptured_PlainMismatch_StillFlagged()
    {
        // No tax captured and the ex-VAT product doesn't match → flagged (the Gjensidige
        // line as it would arrive if the model failed to capture tax — the genuine bug case).
        const string source = "1 GJ Service 1 EA 3337.08 4171.35";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "REDACTED-ITEM", OrderDate: "", Currency: "NOK", BuyerName: "Gjensidige Forsikring",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "GJ", "Service", 1, "EA", 3337.08, 4171.35),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.ReviewLineNumbers.Should().Contain(1);
        result.ReviewReasons![1].Should().Contain("quantity × unit price does not match the stated line amount");
    }
}
