using FluentAssertions;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

public class FingerprintBoostTests
{
    /// <summary>A scored heuristic detection — the only shape a boost is meaningful on.</summary>
    private static DetectedFormat BaseCsv(double confidence) =>
        new("csv", confidence, "CsvOrderParser", "PO-1", null, 3, new List<string> { "CSV pass." },
            Basis: FormatDetectionBasis.Heuristic);

    /// <summary>
    /// A deterministic detection: <c>%PDF-</c> matched, so there is no number and nothing to boost.
    /// Not reachable through the controller today — only the CSV arm emits ColumnHeaders, so only a
    /// CSV can reach a fingerprint lookup — but <see cref="FingerprintBoost.Apply"/> is a public pure
    /// function taking any <see cref="DetectedFormat"/>, and this is the shape it must not corrupt.
    /// </summary>
    private static DetectedFormat BasePdf() =>
        new("pdf", null, "PdfOrderParser", "PO-1", null, null, new List<string> { "First 5 bytes are %PDF- (PDF magic)." },
            Basis: FormatDetectionBasis.MagicBytes);

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
        var match = new SchemaFingerprintMatch("hash", SeenCount: 1, "Acme", "csv", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.SeenCount.Should().Be(1);
        result.Confidence.Should().BeApproximately(0.68, 0.0001, "0.65 + 0.03 for one prior sighting");
        result.Reasoning.Should().Contain(r => r.Contains("layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_CapsBoostAtMax()
    {
        var detected = BaseCsv(0.50);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 100, "Acme", "csv", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().BeApproximately(0.50 + FingerprintBoost.MaxBoost, 0.0001,
            "the additive boost is capped regardless of how many sightings");
    }

    [Fact]
    public void Apply_NeverExceedsCeiling()
    {
        var detected = BaseCsv(0.95);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 50, "Acme", "csv", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().BeLessOrEqualTo(FingerprintBoost.ConfidenceCeiling);
    }

    [Fact]
    public void Apply_IgnoresNonPositiveSeenCount()
    {
        var detected = BaseCsv(0.65);
        var match = new SchemaFingerprintMatch("hash", SeenCount: 0, "Acme", "csv", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().Be(0.65);
        result.SeenCount.Should().BeNull();
    }

    // ── The controls for the fabricated-confidence fix ──────────────────────────────────────────

    [Fact]
    public void Apply_DoesNotInventAConfidence_ForADeterministicDetection()
    {
        // A test that only exercises the CSV path passes without touching the defect: the whole
        // point is that a magic-byte FACT must not acquire a number by being recognised.
        var detected = BasePdf();
        var match = new SchemaFingerprintMatch("hash", SeenCount: 5, "Acme", "pdf", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.Confidence.Should().BeNull(
            "0.03 × 5 added to a byte comparison would invent the doubt first and then shrink it");
        result.Basis.Should().Be(FormatDetectionBasis.MagicBytes);
    }

    [Fact]
    public void Apply_StillReportsSeenCount_ForADeterministicDetection()
    {
        // Suppressing the boost must not suppress the recognition. SeenCount is a real count of real
        // prior parses — the honest half — and the wizard renders it as its own chip.
        var detected = BasePdf();
        var match = new SchemaFingerprintMatch("hash", SeenCount: 5, "Acme", "pdf", new List<Guid>());

        var result = FingerprintBoost.Apply(detected, match);

        result.SeenCount.Should().Be(5);
        result.Reasoning.Should().Contain(r => r.Contains("parsed this exact layout 5 time(s) before"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Apply_DoesNotNarrateTheArithmetic_OnAnyPath(int seenCount)
    {
        // The reasoning list is rendered verbatim to the operator in the upload wizard's "Why this
        // format was detected" disclosure. It used to end "Confidence boosted from 0.65 to 0.80",
        // presenting a sum over two hand-tuned constants as if it were evidence.
        var match = new SchemaFingerprintMatch("hash", seenCount, "Acme", "csv", new List<Guid>());

        foreach (var result in new[]
                 {
                     FingerprintBoost.Apply(BaseCsv(0.65), match),
                     FingerprintBoost.Apply(BasePdf(), match),
                 })
        {
            result.Reasoning.Should().NotContain(
                r => r.Contains("boost", StringComparison.OrdinalIgnoreCase),
                "the narration may state the count, never the arithmetic performed on the score");
        }
    }
}
