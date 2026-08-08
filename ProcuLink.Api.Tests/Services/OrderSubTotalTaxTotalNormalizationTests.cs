using FluentAssertions;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Additive follow-up to #19 (which fixed GrandTotal only, deferring SubTotal). Same root cause as
/// the "grand_total = 0" incident: a parser/LLM that cannot read a header total emits 0
/// (or a negative) rather than null, and the ingest persisted it verbatim. A stored 0 is HARMFUL
/// because <c>MappedTransformService.BuildHeaderRow</c> only DERIVES the total from the line sum
/// when the stored value is NULL — a stored 0 is delivered as a literal "0".
///
/// <para><see cref="OrderIngestionService.NormalizeExtractedSubTotal"/> mirrors the shipped
/// <c>NormalizeExtractedGrandTotal</c> (non-positive → null; a genuine zero-value order derives back
/// to 0 downstream). <see cref="OrderIngestionService.NormalizeExtractedTaxTotal"/> scrubs only a
/// NEGATIVE tax total — a stated 0 tax is legitimate (tax-free / intra-EU reverse charge) and is
/// preserved.</para>
/// </summary>
public class OrderSubTotalTaxTotalNormalizationTests
{
    // ── SubTotal: non-positive → null (mirrors GrandTotal) ──────────────────────

    [Fact]
    public void SubTotal_Zero_NormalizesToNull()
        => OrderIngestionService.NormalizeExtractedSubTotal(0m).Should().BeNull();

    [Fact]
    public void SubTotal_Negative_NormalizesToNull()
        => OrderIngestionService.NormalizeExtractedSubTotal(-5m).Should().BeNull();

    [Fact]
    public void SubTotal_Positive_IsPreserved()
        => OrderIngestionService.NormalizeExtractedSubTotal(752.40m).Should().Be(752.40m);

    [Fact]
    public void SubTotal_Null_StaysNull()
        => OrderIngestionService.NormalizeExtractedSubTotal(null).Should().BeNull();

    // ── TaxTotal: a stated 0 is legitimate; only negatives are scrubbed ─────────

    [Fact]
    public void TaxTotal_Zero_IsLegitimate_AndPreserved()
        => OrderIngestionService.NormalizeExtractedTaxTotal(0m).Should().Be(0m);

    [Fact]
    public void TaxTotal_Negative_NormalizesToNull()
        => OrderIngestionService.NormalizeExtractedTaxTotal(-1m).Should().BeNull();

    [Fact]
    public void TaxTotal_Positive_IsPreserved()
        => OrderIngestionService.NormalizeExtractedTaxTotal(20m).Should().Be(20m);

    [Fact]
    public void TaxTotal_Null_StaysNull()
        => OrderIngestionService.NormalizeExtractedTaxTotal(null).Should().BeNull();
}
