using ProcuLink.Core.Services.Detection;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// Company-name and email-domain normalisation, the two members flow sender matching and inbound
/// email routing still call. No database.
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
}
