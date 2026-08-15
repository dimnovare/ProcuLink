using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Pins the two properties duplicate detection rests on: a minted placeholder is unique even within
/// one second, and a placeholder never carries a comparison key.
/// </summary>
public class PoNumberIdentityTests
{
    private static readonly DateTime FixedSecond = new(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The original defect verbatim: <c>$"PO-{now:yyyyMMddHHmmss}"</c> is truncated to whole seconds,
    /// so every stub created in the same second got the byte-identical string.
    /// </summary>
    [Fact]
    public void MakePlaceholder_InTheSameSecond_DiffersPerOrder()
    {
        var a = PoNumberIdentity.MakePlaceholder(FixedSecond, Guid.NewGuid());
        var b = PoNumberIdentity.MakePlaceholder(FixedSecond, Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.StartsWith("PO-20260815090000-", a);
        Assert.StartsWith("PO-20260815090000-", b);
    }

    /// <summary>Same order id + same instant is the same string — a retry must not invent a new PO number.</summary>
    [Fact]
    public void MakePlaceholder_IsDeterministic_ForTheSameOrderAndInstant()
    {
        var orderId = Guid.NewGuid();

        Assert.Equal(
            PoNumberIdentity.MakePlaceholder(FixedSecond, orderId),
            PoNumberIdentity.MakePlaceholder(FixedSecond, orderId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithNoSuppliedNumber_MintsAPlaceholderAndNoComparisonKey(string? supplied)
    {
        var (value, normalized) = PoNumberIdentity.Resolve(supplied, FixedSecond, Guid.NewGuid());

        Assert.StartsWith(PoNumberIdentity.PlaceholderPrefix, value);
        Assert.Null(normalized);
    }

    /// <summary>
    /// Anti-vacuity for the case above: a real PO number DOES get a key, so "normalized is null" is
    /// carrying information rather than always being null.
    /// </summary>
    [Fact]
    public void Resolve_WithARealNumber_KeepsItVerbatimAndProducesAKey()
    {
        var (value, normalized) = PoNumberIdentity.Resolve("  po-4471 ", FixedSecond, Guid.NewGuid());

        Assert.Equal("po-4471", value);        // trimmed, but NOT case-folded: this is what the supplier sees
        Assert.Equal("PO-4471", normalized);   // folded only for comparison
    }

    [Fact]
    public void Normalize_FoldsCaseAndPaddingOnly()
    {
        Assert.Equal("PO-4471", PoNumberIdentity.Normalize(" po-4471 "));
        Assert.Equal("PO-4471", PoNumberIdentity.Normalize("PO-4471"));
        Assert.Null(PoNumberIdentity.Normalize("   "));
        Assert.Null(PoNumberIdentity.Normalize(null));

        // Deliberately NOT folded — different supplier-facing identifiers.
        Assert.NotEqual(PoNumberIdentity.Normalize("PO-1001"), PoNumberIdentity.Normalize("PO1001"));
        Assert.NotEqual(PoNumberIdentity.Normalize("PO-1001"), PoNumberIdentity.Normalize("PO-01001"));
    }
}
