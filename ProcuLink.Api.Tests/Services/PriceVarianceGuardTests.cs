using ProcuLink.Core.Services.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Deterministic unit tests for the pure <see cref="PriceVarianceGuard"/> evaluator, including the
/// EU comma-decimal case that proves the catalog price is parsed locale-aware (NOT corrupted 1000×).
/// </summary>
public class PriceVarianceGuardTests
{
    [Fact]
    public void Disabled_guard_never_flags()
    {
        var g = new PriceVarianceGuard(Enabled: false, ThresholdPercent: 1m);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "1.00").Breached);
    }

    [Fact]
    public void Within_threshold_does_not_flag()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        // 110 vs 100 = +10% ≤ 20%
        Assert.False(g.Breaches(poUnitPrice: 110m, catalogPriceRaw: "100.00").Breached);
    }

    [Fact]
    public void Exactly_at_threshold_does_not_flag()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        // 120 vs 100 = exactly +20% → NOT a breach (strictly greater-than holds).
        Assert.False(g.Breaches(poUnitPrice: 120m, catalogPriceRaw: "100").Breached);
    }

    [Fact]
    public void Beyond_threshold_flags_with_signed_percent()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        var r = g.Breaches(poUnitPrice: 130m, catalogPriceRaw: "100.00"); // +30%
        Assert.True(r.Breached);
        Assert.Equal(30m, decimal.Round(r.VariancePercent, 0));
    }

    [Fact]
    public void Below_threshold_drop_flags_with_negative_signed_percent()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        var r = g.Breaches(poUnitPrice: 50m, catalogPriceRaw: "100.00"); // -50%
        Assert.True(r.Breached);
        Assert.Equal(-50m, decimal.Round(r.VariancePercent, 0));
    }

    [Fact]
    public void EU_comma_decimal_catalog_price_parses_correctly()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 5m);
        // catalog "1.234,56" (EU) = 1234.56; PO 1234.56 → 0% variance, NOT a 1000× misread.
        Assert.False(g.Breaches(poUnitPrice: 1234.56m, catalogPriceRaw: "1.234,56").Breached);
    }

    [Fact]
    public void EU_bare_comma_decimal_parses_correctly()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 5m);
        // catalog "73,22" (EU) = 73.22; PO 73.22 → 0% variance, NOT 7322 (the 100× corruption class).
        Assert.False(g.Breaches(poUnitPrice: 73.22m, catalogPriceRaw: "73,22").Breached);
    }

    [Fact]
    public void US_thousands_then_dot_decimal_parses_correctly()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 5m);
        // catalog "1,234.56" (US) = 1234.56; PO 1234.56 → 0% variance.
        Assert.False(g.Breaches(poUnitPrice: 1234.56m, catalogPriceRaw: "1,234.56").Breached);
    }

    [Fact]
    public void Currency_symbol_and_whitespace_are_tolerated()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 5m);
        // "€ 100,00" = 100.00; PO 100 → 0% variance.
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "€ 100,00").Breached);
    }

    [Fact]
    public void Letters_in_catalog_price_are_refused_not_corrupted()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 1m);
        // "1.5e2" must NOT become 1.52 (silent mis-price) — it is ambiguous → no catalog price → no flag.
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "1.5e2").Breached);
    }

    [Fact]
    public void Missing_or_zero_catalog_price_never_flags()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 1m);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: null).Breached);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "").Breached);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "0").Breached);
    }

    [Fact]
    public void Zero_or_negative_po_price_never_flags()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 1m);
        Assert.False(g.Breaches(poUnitPrice: 0m, catalogPriceRaw: "100").Breached);
        Assert.False(g.Breaches(poUnitPrice: -5m, catalogPriceRaw: "100").Breached);
    }
}
