using FluentAssertions;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// Tests the pure pre-fill policy that turns a supplier's learned schema mapping into a line-level
/// suggestion: it surfaces a learned buyer→supplier code as a (never-auto-applied) suggestion with
/// the <c>learned-schema-mapping</c> provenance, and returns null when there is nothing to pre-fill.
/// </summary>
public class LearnedMappingPrefillTests
{
    private static readonly Dictionary<string, string> Learned = new()
    {
        ["abc-1"] = "SUP-99",
        ["xyz-2"] = "SUP-42",
    };

    [Fact]
    public void TryBuild_ReturnsSuggestion_WhenBuyerCodeIsKnown()
    {
        var s = LearnedMappingPrefill.TryBuild("ABC-1", Learned);

        s.Should().NotBeNull();
        s!.SupplierItemCode.Should().Be("SUP-99");
        s.Provenance.Should().Be("learned-schema-mapping");
        s.Confidence.Should().Be(LearnedMappingPrefill.Confidence);
        s.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("ABC-1")]   // uppercase
    [InlineData("  abc-1 ")] // padded
    [InlineData("Abc-1")]   // mixed case
    public void TryBuild_MatchesBuyerCode_CaseAndWhitespaceInsensitively(string buyerCode)
    {
        var s = LearnedMappingPrefill.TryBuild(buyerCode, Learned);

        s.Should().NotBeNull("learned keys are stored normalised (trim + lowercase)");
        s!.SupplierItemCode.Should().Be("SUP-99");
    }

    [Fact]
    public void TryBuild_ReturnsNull_WhenBuyerCodeUnknown()
    {
        LearnedMappingPrefill.TryBuild("not-in-map", Learned).Should().BeNull();
    }

    [Fact]
    public void TryBuild_ReturnsNull_WhenMappingNullOrEmpty()
    {
        LearnedMappingPrefill.TryBuild("abc-1", null).Should().BeNull();
        LearnedMappingPrefill.TryBuild("abc-1", new Dictionary<string, string>()).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryBuild_ReturnsNull_ForBlankBuyerCode(string? buyerCode)
    {
        LearnedMappingPrefill.TryBuild(buyerCode!, Learned).Should().BeNull();
    }

    [Fact]
    public void TryBuild_ReturnsNull_WhenLearnedSupplierCodeIsBlank()
    {
        var withBlank = new Dictionary<string, string> { ["abc-1"] = "   " };
        LearnedMappingPrefill.TryBuild("abc-1", withBlank).Should().BeNull();
    }
}
