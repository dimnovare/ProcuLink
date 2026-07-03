using FluentAssertions;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Pins the persistence-seam normalization of an UNCAPTURED extracted grand total.
///
/// PDF/XLSX extraction can emit <c>grand_total: 0</c> (or a bogus non-positive value) when it
/// cannot read a header total. Persisting that literal <c>0</c> defeats downstream derivation —
/// <c>MappedTransformService.DeriveGrandTotal</c> derives <c>sum(Qty*UnitPrice)</c> ONLY when the
/// stored <c>GrandTotal</c> is <c>NULL</c> — so a wrong <c>"0"</c> would be emitted into delivered
/// supplier documents. <see cref="OrderIngestionService"/> normalizes any non-positive extracted
/// grand total to <c>NULL</c> at persistence, letting line-sum derivation take over.
///
/// Sibling display-side fix: project-proculink commit cf0ad05 (hide total when unknown).
/// </summary>
public class OrderGrandTotalNormalizationTests
{
    [Fact]
    public void ExtractedZero_NormalizesToNull()
    {
        OrderIngestionService.NormalizeExtractedGrandTotal(0m).Should().BeNull();
    }

    [Fact]
    public void ExtractedNegative_NormalizesToNull()
    {
        // A negative grand total is never a legitimately capturable PO value — treat it as
        // uncaptured so line-sum derivation (the safer source) takes over downstream.
        OrderIngestionService.NormalizeExtractedGrandTotal(-5m).Should().BeNull();
    }

    [Fact]
    public void ExtractedPositive_IsPreserved()
    {
        OrderIngestionService.NormalizeExtractedGrandTotal(120m).Should().Be(120m);
    }

    [Fact]
    public void Unset_StaysNull()
    {
        OrderIngestionService.NormalizeExtractedGrandTotal(null).Should().BeNull();
    }
}
