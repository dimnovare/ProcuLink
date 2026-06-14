using System.Linq;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Phase 2 (D slice): the four new lossless-mapping validation operators are SEEDED in the global
/// <see cref="RuleCatalog"/> so an org can bind them. Each seed's (fieldPath, operator) must match
/// the executor's resolvable field paths: <c>sourceDate</c>/date_sanity and <c>shipToCity</c>/not_label
/// and <c>shipToVat</c>/vat_format are ORDER scope (resolved from the order + its parties + the raw
/// SourceCapture bag); <c>lineAmount</c>/line_amount_reconcile is LINE scope.
/// </summary>
public class RuleCatalogNewSeedsTests
{
    [Theory]
    [InlineData("order", "sourceDate", "date_sanity")]
    [InlineData("order", "shipToCity", "not_label")]
    [InlineData("line", "lineAmount", "line_amount_reconcile")]
    [InlineData("order", "shipToVat", "vat_format")]
    public void Catalog_contains_the_new_seed(string scope, string field, string op)
    {
        var code = RuleCatalog.CodeFor(field, op);
        var entry = RuleCatalog.Entries.SingleOrDefault(e => e.Code == code);
        Assert.NotNull(entry);
        Assert.Equal(scope, entry!.Scope);
        Assert.Equal(field, entry.FieldPath);
        Assert.Equal(op, entry.Operator);
    }

    [Theory]
    [InlineData("sourceDate", "date_sanity")]
    [InlineData("shipToCity", "not_label")]
    [InlineData("lineAmount", "line_amount_reconcile")]
    [InlineData("shipToVat", "vat_format")]
    public void New_seeds_are_advisory_warnings_not_hard_blocks(string field, string op)
    {
        // The new operators flag for review (advisory) rather than hard-block, since printed-date /
        // label / VAT formats evolve. Each seed's default severity must be "warning".
        var entry = RuleCatalog.Entries.Single(e => e.Code == RuleCatalog.CodeFor(field, op));
        Assert.Equal("warning", entry.DefaultSeverity);
    }

    [Fact]
    public void New_seeds_carry_standards_references()
    {
        foreach (var (field, op) in new[]
                 {
                     ("sourceDate", "date_sanity"), ("shipToCity", "not_label"),
                     ("lineAmount", "line_amount_reconcile"), ("shipToVat", "vat_format"),
                 })
        {
            var entry = RuleCatalog.Entries.Single(e => e.Code == RuleCatalog.CodeFor(field, op));
            Assert.False(string.IsNullOrWhiteSpace(entry.UblRef), $"{entry.Code} missing UBL ref");
        }
    }
}
