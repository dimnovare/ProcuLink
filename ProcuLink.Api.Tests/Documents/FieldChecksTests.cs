using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class FieldChecksTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly Dictionary<Guid, CheckLookup> NoLookups = [];

    private static ReadResult Read(string key, string label, string value) =>
        new("Invoice", [new ReadField(key, label, value, "text", null, 0.99, null, false, true, false)]);

    private static FlowFieldList Checked(string key, string label, FieldCheck check) =>
        new([new FlowField(key, label, FieldTypes.Text, null, false, check)]);

    [Theory]
    [InlineData("one_of", "eur", null, null, true)]
    [InlineData("one_of", "GBP", null, null, false)]
    [InlineData("between", "250", "0", "1000", true)]
    [InlineData("between", "1.500", "0", "1000", false)] // ambiguous: marked, never passed
    [InlineData("between", "abc", null, "1000", false)]
    [InlineData("between", "-5", "0", null, false)]
    public void Run_MarksAFailingValue_AndOnlyThat(string op, string value, string? min, string? max, bool passes)
    {
        var check = new FieldCheck(op, ["EUR", " USD ", ""], min, max);
        var failed = FieldChecks.Run(Read("amount", "Amount", value), Checked("amount", "Amount", check), NoLookups, Today);

        failed.Where(c => c.Code == DocumentChecks.CustomCheck && c.FieldKey == "amount" && !c.Passed).Should().HaveCount(passes ? 0 : 1);
        failed.Should().HaveCount(passes ? 0 : 1);
    }

    [Fact]
    public void NotAfterToday_AllowsUtcTomorrow_AndMarksTheDayAfter_AndAnUnreadableDate()
    {
        var fields = Checked("invoice_date", "Invoice date", new FieldCheck(FieldCheckOps.NotAfterToday));
        IReadOnlyList<DocumentCheck> Run(string value) => FieldChecks.Run(Read("invoice_date", "Invoice date", value), fields, NoLookups, Today);

        Run("2026-11-25").Should().BeEmpty();
        Run("2026-11-26").Should().ContainSingle().Which.Message.Should().Be("Invoice date should be a date no later than today.");
        Run("31/02/2026").Should().ContainSingle().Which.Message.Should().Be("Invoice date should be a date no later than today.");
    }

    [Fact]
    public void InLookup_MatchesTrimmedCaseInsensitive_MarksAMissingLookup_AndSkipsAnEmptyValue()
    {
        var id = Guid.NewGuid();
        var lookups = new Dictionary<Guid, CheckLookup> { [id] = new("Cost centres", new HashSet<string>(["CC-10"], StringComparer.OrdinalIgnoreCase)) };
        IReadOnlyList<DocumentCheck> Run(string value, Guid lookupId) => FieldChecks.Run(Read("cost_centre", "Cost centre", value),
            Checked("cost_centre", "Cost centre", new FieldCheck(FieldCheckOps.InLookup, LookupId: lookupId)), lookups, Today);

        Run(" cc-10 ", id).Should().BeEmpty();
        Run("CC-99", id).Should().ContainSingle().Which.Message.Should().Be("Cost centre isn't in the lookup \"Cost centres\".");
        Run("CC-10", Guid.NewGuid()).Should().ContainSingle().Which.Message.Should().Be("Cost centre couldn't be checked: its lookup was removed.");
        Run("", id).Should().BeEmpty();
    }
}
