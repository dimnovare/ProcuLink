using FluentAssertions;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

public class FingerprintBoostTests
{
    private static DetectedFormat BaseCsv(double confidence) =>
        new("csv", confidence, "CsvOrderParser", "PO-1", null, 3, new List<string> { "CSV pass." });

    [Fact]
    public void Apply_LeavesResultUnchanged_WhenNoMatch()
    {
        var detected = BaseCsv(0.65);

        var result = FingerprintBoost.Apply(detected, null);

        result.Confidence.Should().Be(0.65);
        result.SeenCount.Should().BeNull();
        result.Reasoning.Should().BeEquivalentTo(detected.Reasoning);
    }

    [Fact]
    public void Apply_BoostsConfidenceAndSetsSeenCount_WhenMatched()
    {
        var detected = BaseCsv(0.65);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 1, "Acme", "csv");

        var result = FingerprintBoost.Apply(detected, match);

        result.SeenCount.Should().Be(1);
        result.Confidence.Should().BeApproximately(0.68, 0.0001, "0.65 + 0.03 for one prior sighting");
        result.Reasoning.Should().Contain(r => r.Contains("layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_CapsBoostAtMax()
    {
        var detected = BaseCsv(0.50);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 100, "Acme", "csv");

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().BeApproximately(0.50 + FingerprintBoost.MaxBoost, 0.0001,
            "the additive boost is capped regardless of how many sightings");
    }

    [Fact]
    public void Apply_NeverExceedsCeiling()
    {
        var detected = BaseCsv(0.95);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 50, "Acme", "csv");

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().BeLessOrEqualTo(FingerprintBoost.ConfidenceCeiling);
    }

    [Fact]
    public void Apply_IgnoresNonPositiveSeenCount()
    {
        var detected = BaseCsv(0.65);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 0, "Acme", "csv");

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().Be(0.65);
        result.SeenCount.Should().BeNull();
    }
}
