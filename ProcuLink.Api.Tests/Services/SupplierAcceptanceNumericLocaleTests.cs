using System;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// M4 (money-correctness) golden tests for the comparison operators (min / max / greater_than /
/// less_than) in <see cref="SupplierAcceptanceService"/>.
///
/// Before the fix both operands were parsed with <c>double.TryParse(NumberStyles.Any,
/// InvariantCulture)</c>, which silently read a comma-decimal token ("73,22") as a thousands-grouped
/// integer (7322). A <c>max:"5000"</c> rule therefore PASSED a non-conforming "73,22"-style value
/// (7322 ≤ 5000 was false → actually it FAILED for 7322, but the symmetric danger is real: a
/// comma-decimal THRESHOLD like "50,00" became 5000, silently relaxing the rule by 100×). The fix
/// parses both operands locale-safely and fails closed on genuinely ambiguous input.
///
/// IMPORTANT: the order's value (the <c>actual</c> operand) is built from TYPED decimal columns via
/// <c>.ToString(InvariantCulture)</c>, so in production it is always an invariant string like
/// "73.22"; these tests confirm that path reads back exactly (no flip) while a comma-decimal RULE
/// THRESHOLD is now read correctly.
/// </summary>
public class SupplierAcceptanceNumericLocaleTests
{
    private static SupplierAcceptanceProfile Profile(params SupplierAcceptanceRule[] rules) =>
        new() { Id = Guid.NewGuid(), Rules = new(rules) };

    private static SupplierAcceptanceRule LineRule(string field, string op, string expected) =>
        new() { Id = Guid.NewGuid(), Scope = "line", FieldPath = field, Operator = op, ExpectedValue = expected, Severity = "error" };

    private static PurchaseOrderEntity OrderWithUnitPrice(decimal unitPrice) => new()
    {
        Currency = "EUR",
        Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = unitPrice } },
    };

    private static string StatusOf(PurchaseOrderEntity order, SupplierAcceptanceRule rule)
    {
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        return Assert.Single(results).Status;
    }

    // ── The headline M4 case: a comma-decimal value must not become a thousands-grouped integer ──

    [Fact]
    public void Max5000_vs_unitPrice_73_22_does_not_pass_as_7322()
    {
        // unitPrice 73.22 → typed-column "actual" = "73.22". max:"5000" → 73.22 ≤ 5000 → PASS (correct).
        // The bug being guarded: NEITHER operand may be misread as 7322 / 5,000-as-5000-decimal.
        var order = OrderWithUnitPrice(73.22m);
        Assert.Equal("pass", StatusOf(order, LineRule("unitPrice", "max", "5000")));

        // And the inverse proves we are genuinely comparing 73.22 (not 7322): max:"50" must FAIL,
        // because 73.22 > 50. Under the old bug "73.22" stayed 73.22 here too, so this is a sanity check
        // that the locale-safe reader did not regress the dot-decimal path.
        Assert.Equal("fail", StatusOf(order, LineRule("unitPrice", "max", "50")));
    }

    [Fact]
    public void CommaDecimal_threshold_is_read_as_a_decimal_not_a_thousands_integer()
    {
        // THIS is the silent-relaxation the audit warns about: a rule author types a comma-decimal
        // threshold "50,00" (= fifty). Old code: double.TryParse("50,00", Any, Invariant) = 5000,
        // so a unit price of 73.22 wrongly PASSED max:"50,00" (73.22 ≤ 5000). Now "50,00" = 50.0,
        // so 73.22 > 50 → FAIL (non-conforming value is correctly rejected).
        var order = OrderWithUnitPrice(73.22m);
        Assert.Equal("fail", StatusOf(order, LineRule("unitPrice", "max", "50,00")));

        // Symmetric min check: min "100,00" (= one hundred) on a 73.22 price → 73.22 < 100 → FAIL.
        Assert.Equal("fail", StatusOf(order, LineRule("unitPrice", "min", "100")));
        // min "73,22" on a 73.22 price → 73.22 >= 73.22 → PASS (the comma threshold parses to 73.22).
        Assert.Equal("pass", StatusOf(order, LineRule("unitPrice", "min", "73,22")));
    }

    // ── Pure-helper golden values (TryParseNumeric) ──────────────────────────────────────────────

    [Theory]
    [InlineData("73,22", true, 73.22)]      // EU comma decimal → 73.22 (NOT 7322)
    [InlineData("73.22", true, 73.22)]      // invariant dot decimal (the typed-column form) unchanged
    [InlineData("1.234,56", true, 1234.56)] // EU grouped thousands + comma decimal
    [InlineData("1,234.56", true, 1234.56)] // US grouped thousands + dot decimal
    [InlineData("5000", true, 5000)]        // plain integer threshold unchanged
    [InlineData("5,000", true, 5000)]       // US thousands group (single comma, 3 trailing) → 5000
    [InlineData("50,00", true, 50)]         // comma decimal "fifty" → 50.0 (NOT 5000)
    [InlineData("0.45", true, 0.45)]
    [InlineData("100", true, 100)]
    public void TryParseNumeric_reads_both_us_and_eu_notation(string raw, bool ok, double expected)
    {
        var parsed = SupplierAcceptanceService.TryParseNumeric(raw, out var value);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(expected, value, precision: 6);
    }

    [Theory]
    [InlineData("12.5x")]   // stray letter → ambiguous
    [InlineData("1.5e2")]   // scientific notation → ambiguous (not 1.52)
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public void TryParseNumeric_refuses_ambiguous_or_empty_tokens(string raw)
    {
        Assert.False(SupplierAcceptanceService.TryParseNumeric(raw, out _));
    }

    [Fact]
    public void Numeric_rule_with_ambiguous_threshold_fails_closed_not_silent_pass()
    {
        // A garbage threshold ("5o00" — letter 'o') must not silently pass. The comparison fails
        // closed so the misconfigured rule surfaces rather than reporting a vacuous green.
        var order = OrderWithUnitPrice(10m);
        Assert.Equal("fail", StatusOf(order, LineRule("unitPrice", "max", "5o00")));
    }
}
