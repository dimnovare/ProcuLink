using ProcuLink.Core.Services.Detection;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// The pure half of supplier auto-detect: normalisation, score combination, ranking.
/// No database — these pin the judgement itself, which is the part that decides which
/// supplier an operator is shown first.
/// </summary>
public class SupplierSuggestionScoringTests
{
    // ── Company-name normalisation ────────────────────────────────────────────

    [Theory]
    [InlineData("Acme GmbH", "acme")]
    [InlineData("  ACME   gmbh ", "acme")]
    [InlineData("Acme Widgets Ltd.", "acme widgets")]
    [InlineData("Acme Widgets, Inc.", "acme widgets")]
    [InlineData("Diip Solutions OÜ", "diip solutions")]
    [InlineData("Nordic Parts A/S", "nordic parts")]
    [InlineData("Van der Berg B.V.", "van der berg")]
    public void NormalizeCompanyName_stripsCasePunctuationAndLegalSuffix(string raw, string expected)
    {
        Assert.Equal(expected, SupplierSuggestionScoring.NormalizeCompanyName(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GmbH")]   // a bare legal suffix normalises away to nothing — never a match key
    public void NormalizeCompanyName_returnsNull_forNothingUsable(string? raw)
    {
        Assert.Null(SupplierSuggestionScoring.NormalizeCompanyName(raw));
    }

    // ── Identifier normalisation + matching ───────────────────────────────────

    [Theory]
    [InlineData("EE 1012 3456 7", "EE101234567")]
    [InlineData("de-123.456.789", "DE123456789")]
    [InlineData("  1111111111116  ", "1111111111116")]
    public void NormalizeIdentifier_stripsSeparatorsAndUppercases(string raw, string expected)
    {
        Assert.Equal(expected, SupplierSuggestionScoring.NormalizeIdentifier(raw));
    }

    [Fact]
    public void IdentifiersMatch_isTrue_forSameIdWrittenDifferently()
    {
        Assert.True(SupplierSuggestionScoring.IdentifiersMatch("EE 101234567", "ee101234567"));
    }

    [Fact]
    public void IdentifiersMatch_toleratesAnIsoCountryPrefixOnExactlyOneSide()
    {
        // The single most common real-world variance: the document carries the prefixed VAT and
        // the supplier profile carries the bare registration number (or the reverse).
        Assert.True(SupplierSuggestionScoring.IdentifiersMatch("EE101234567", "101234567"));
        Assert.True(SupplierSuggestionScoring.IdentifiersMatch("101234567", "EE101234567"));
    }

    [Theory]
    [InlineData("EE101234567", "EE101234568")]  // one digit apart — never a match, no fuzziness here
    [InlineData("DE101234567", "FR101234567")]  // same number, different country: two different ids
    [InlineData("101234567", "1234567")]        // prefix-of, but not a 2-letter country prefix
    [InlineData(null, "EE101234567")]
    [InlineData("EE101234567", null)]
    [InlineData("", "")]
    public void IdentifiersMatch_isFalse_forAnythingElse(string? left, string? right)
    {
        Assert.False(SupplierSuggestionScoring.IdentifiersMatch(left, right));
    }

    // ── Domain normalisation ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Orders@Acme.EXAMPLE", "acme.example")]
    [InlineData("www.acme.example", "acme.example")]
    [InlineData("acme.example.", "acme.example")]
    [InlineData("  ACME.example ", "acme.example")]
    public void NormalizeDomain_lowercasesAndStripsAddressAndWwwAndTrailingDot(string raw, string expected)
    {
        Assert.Equal(expected, SupplierSuggestionScoring.NormalizeDomain(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    public void NormalizeDomain_returnsNull_forNothingUsable(string? raw)
    {
        Assert.Null(SupplierSuggestionScoring.NormalizeDomain(raw));
    }

    // ── Score combination ─────────────────────────────────────────────────────

    [Fact]
    public void Combine_sumsContributions()
    {
        var score = SupplierSuggestionScoring.Combine(new[]
        {
            new SupplierSignalContribution(SupplierSignalKind.Name, 0.20, "name"),
            new SupplierSignalContribution(SupplierSignalKind.CatalogOverlap, 0.25, "catalog"),
        });

        Assert.Equal(0.45, score, 3);
    }

    [Fact]
    public void Combine_neverReachesCertainty()
    {
        // Every signal firing at full weight still must not read as 1.0 — the same ceiling the
        // fingerprint boost already respects, for the same reason: this is a heuristic.
        var everything = new[]
        {
            new SupplierSignalContribution(SupplierSignalKind.Identity, SupplierSuggestionScoring.IdentityMatchWeight, "id"),
            new SupplierSignalContribution(SupplierSignalKind.SenderDomain, SupplierSuggestionScoring.SenderDomainWeight, "domain"),
            new SupplierSignalContribution(SupplierSignalKind.Layout, SupplierSuggestionScoring.LayoutWeight, "layout"),
            new SupplierSignalContribution(SupplierSignalKind.CatalogOverlap, SupplierSuggestionScoring.CatalogOverlapWeight, "catalog"),
            new SupplierSignalContribution(SupplierSignalKind.Name, SupplierSuggestionScoring.NameMatchWeight, "name"),
            new SupplierSignalContribution(SupplierSignalKind.SenderDomainHistory, SupplierSuggestionScoring.SenderDomainHistoryWeight, "history"),
        };

        Assert.True(everything.Sum(s => s.Contribution) > 1.0, "precondition: the raw weights must overflow 1.0");
        Assert.Equal(FingerprintBoost.ConfidenceCeiling, SupplierSuggestionScoring.Combine(everything), 3);
    }

    [Fact]
    public void Combine_ofNothing_isZero()
    {
        Assert.Equal(0, SupplierSuggestionScoring.Combine(Array.Empty<SupplierSignalContribution>()));
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    private static (Guid, string, IReadOnlyList<SupplierSignalContribution>) Candidate(
        string name, double contribution, string signal = SupplierSignalKind.Name) =>
        (Guid.NewGuid(), name, new[] { new SupplierSignalContribution(signal, contribution, "detail") });

    [Fact]
    public void Rank_ordersByScoreDescending_and_numbersFromOne()
    {
        var ranked = SupplierSuggestionScoring.Rank(new[]
        {
            Candidate("Weak", 0.20),
            Candidate("Strong", 0.60),
            Candidate("Middling", 0.40),
        });

        Assert.Equal(new[] { "Strong", "Middling", "Weak" }, ranked.Select(r => r.SupplierName));
        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(r => r.Rank));
    }

    [Fact]
    public void Rank_keepsAtMostThree()
    {
        var ranked = SupplierSuggestionScoring.Rank(new[]
        {
            Candidate("A", 0.60), Candidate("B", 0.55), Candidate("C", 0.50), Candidate("D", 0.45),
        });

        Assert.Equal(SupplierSuggestionScoring.MaxSuggestions, ranked.Count);
        Assert.DoesNotContain(ranked, r => r.SupplierName == "D");
    }

    [Fact]
    public void Rank_dropsCandidatesBelowTheMinimumScore()
    {
        // A supplier nothing actually pointed at is noise in the operator's face, not a hint.
        var ranked = SupplierSuggestionScoring.Rank(new[]
        {
            Candidate("Real", 0.30),
            Candidate("Noise", SupplierSuggestionScoring.MinimumScore / 2),
        });

        Assert.Single(ranked);
        Assert.Equal("Real", ranked[0].SupplierName);
    }

    [Fact]
    public void Rank_ofNothingAtAll_isEmpty()
    {
        Assert.Empty(SupplierSuggestionScoring.Rank(
            Array.Empty<(Guid, string, IReadOnlyList<SupplierSignalContribution>)>()));
    }

    [Fact]
    public void Rank_breaksTiesDeterministicallyByName_andLeavesTiedScoresTied()
    {
        // This is the shared-layout invariant expressed at the ranking layer: two suppliers the
        // evidence cannot separate must come back with the SAME score, so nothing in the ordering
        // can be read as the layout having picked one of them. Order is by name only so the list
        // is stable between requests.
        var ranked = SupplierSuggestionScoring.Rank(new[]
        {
            Candidate("Zeta Supplies", 0.30, SupplierSignalKind.Layout),
            Candidate("Alpha Supplies", 0.30, SupplierSignalKind.Layout),
        });

        Assert.Equal(new[] { "Alpha Supplies", "Zeta Supplies" }, ranked.Select(r => r.SupplierName));
        Assert.Equal(ranked[0].Score, ranked[1].Score, 6);
    }

    [Fact]
    public void Rank_carriesEverySignalThrough_asProvenance()
    {
        var id = Guid.NewGuid();
        var signals = new[]
        {
            new SupplierSignalContribution(SupplierSignalKind.Identity, 0.45, "VAT matches"),
            new SupplierSignalContribution(SupplierSignalKind.CatalogOverlap, 0.10, "2 of 5 codes"),
        };

        var ranked = SupplierSuggestionScoring.Rank(new[] { (id, "Acme GmbH", (IReadOnlyList<SupplierSignalContribution>)signals) });

        var only = Assert.Single(ranked);
        Assert.Equal(id, only.SupplierId);
        Assert.Equal(0.55, only.Score, 3);
        Assert.Equal(
            new[] { SupplierSignalKind.Identity, SupplierSignalKind.CatalogOverlap },
            only.Signals.Select(s => s.Signal));
    }

    // ── Reason ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildReason_namesTheSupplierAndEverySignalThatFired_inOneSentence()
    {
        var reason = SupplierSuggestionScoring.BuildReason("Acme GmbH", new[]
        {
            new SupplierSignalContribution(SupplierSignalKind.Identity, 0.45, "the VAT number on the document matches"),
            new SupplierSignalContribution(SupplierSignalKind.CatalogOverlap, 0.10, "2 of 5 item codes are in their catalog"),
        });

        Assert.StartsWith("Acme GmbH", reason);
        Assert.Contains("the VAT number on the document matches", reason);
        Assert.Contains("2 of 5 item codes are in their catalog", reason);
        Assert.EndsWith(".", reason);
        Assert.DoesNotContain("\n", reason);
    }

    [Fact]
    public void BuildReason_usesPlainLanguage_noInternalSignalSlugs()
    {
        // The dev-rule string is for the provenance list, never for the sentence a human reads.
        var reason = SupplierSuggestionScoring.BuildReason("Acme GmbH", new[]
        {
            new SupplierSignalContribution(SupplierSignalKind.Layout, 0.30, "this column layout has been used by them before"),
        });

        Assert.DoesNotContain("layout_fingerprint", reason);
        Assert.DoesNotContain("_", reason);
    }
}
