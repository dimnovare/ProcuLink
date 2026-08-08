using FluentAssertions;
using ProcuLink.Core.Catalog;

namespace ProcuLink.Transform.Tests.Catalog;

/// <summary>
/// Tests for the cross-party product-identifier key (MPN as a first-class matching key).
///
/// The normaliser is the ONLY thing standing between "the same manufacturer part written three
/// different ways" and three unmatched order lines: a punchout cXML says <c>REDACTED-ORDER-DATA</c>,
/// a distributor feed says <c>QBT2500BKBTK1</c>, an ERP export says <c>qbt2500 bk btk1</c>. Every
/// non-degenerate case below is a real spelling taken from a fixture in this repo, not a
/// synthetic string.
///
/// The null cases are the load-bearing ones: an empty-string key would match every product that
/// also has no MPN, which is the classic silent mis-match.
/// </summary>
public class ProductKeyNormalizerTests
{
    // U+00A0 written as an escape so the character can never be "tidied" into a plain space by an
    // editor or a diff tool — the whole point of the case is that it is NOT a plain space.
    private const string NonBreakingSpaceInput = "AB\u00A0123";

    [Theory]
    // Real MPNs from the cXML order fixtures in Fixtures/.
    [InlineData("REDACTED-ORDER-DATA", "QBT2500BKBTK1")]  // real-cxml-1.2-ariba-punchout-mpn-differs.xml
    [InlineData("qbt2500 bk btk1", "QBT2500BKBTK1")]  // same part, ERP-export spelling
    [InlineData("REDACTED-ORDER-DATA", "P1058930010")]       // real-cxml-1.1-mpn-equals-supplier-part.xml
    [InlineData("REDACTED-ORDER-DATA", "MWR23SA")]               // cxml-coupa-orderrequest-sek.cxml
    // Nothing comparable survives → null, never "".
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("---", null)]
    public void Normalize_AppliesTheDocumentedRule(string? raw, string? expected)
    {
        ProductKeyNormalizer.Normalize(raw).Should().Be(expected);
    }

    [Fact]
    public void Normalize_NonBreakingSpace_IsStrippedLikeAnyOtherSeparator()
    {
        // Spreadsheet exports leak U+00A0 into part numbers; it is invisible, so a literal
        // comparison fails for a reason nobody can see in the UI.
        NonBreakingSpaceInput.Should().NotBe("AB 123", "the fixture must carry U+00A0, not a plain space");

        ProductKeyNormalizer.Normalize(NonBreakingSpaceInput).Should().Be("AB123");
    }

    [Fact]
    public void Normalize_SeparatorOnlyDifference_Collides_ByDesign()
    {
        // The documented, ACCEPTED collision: stripping separators makes these one key. Asserted
        // explicitly so that anyone who "fixes" it has to come here and read why it is accepted
        // (an MPN hit is a suggestion an operator accepts, and an ambiguous hit yields no
        // suggestion at all — see ProductKeyNormalizer's remarks).
        var withSeparator = ProductKeyNormalizer.Normalize("AB-123");
        var without = ProductKeyNormalizer.Normalize("AB123");

        withSeparator.Should().Be(without);
        withSeparator.Should().Be("AB123");
    }

    [Fact]
    public void Normalize_UpperCasesWithInvariantCulture_NotTheAmbientOne()
    {
        // ToUpper() under tr-TR maps 'i' → 'İ' (U+0130), which is NOT an ASCII letter and would
        // then be stripped — the same input would produce a different key on a Turkish machine.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

            ProductKeyNormalizer.Normalize("mini-i7").Should().Be("MINII7");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // The key is stored on the row; re-normalising a stored key must be a no-op, otherwise a
        // re-sync would silently rewrite existing keys and orphan already-matched lines.
        foreach (var raw in new[] { "REDACTED-ORDER-DATA", "REDACTED-ORDER-DATA", "REDACTED-ORDER-DATA", NonBreakingSpaceInput })
        {
            var once = ProductKeyNormalizer.Normalize(raw);
            once.Should().NotBeNull();
            ProductKeyNormalizer.Normalize(once).Should().Be(once);
        }
    }

    [Fact]
    public void Normalize_LongInputBeyondTheStackallocThreshold_StillNormalises()
    {
        // The implementation switches from stackalloc to a heap buffer above 256 chars; both
        // branches must produce the same key.
        var raw = string.Join("-", Enumerable.Repeat("ab12", 100)); // 499 chars, > 256
        var expected = string.Concat(Enumerable.Repeat("AB12", 100));

        ProductKeyNormalizer.Normalize(raw).Should().Be(expected);
    }
}
