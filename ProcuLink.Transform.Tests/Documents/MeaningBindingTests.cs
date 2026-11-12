using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Transform.Tests.Documents;

public class MeaningBindingTests
{
    [Fact]
    public void Every_meaning_has_plain_aliases()
    {
        foreach (var m in FieldMeanings.All)
        {
            FieldMeanings.Aliases.Should().ContainKey(m);
            FieldMeanings.Aliases[m].Length.Should().BeInRange(3, 8, m);
            FieldMeanings.Aliases[m].Should().OnlyContain(a => a.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)), m);
        }
        FieldMeanings.Aliases.Keys.Should().BeSubsetOf(FieldMeanings.All);
    }

    [Fact]
    public void Leaf_binds_by_meaning_and_the_name_match_wins()
    {
        var fields = new FlowFieldList([
            new FlowField("inv_num", "Reference", "text", FieldMeanings.DocumentNumber, false),
            new FlowField("other", "Other", "text", null, false)]);
        var slots = new List<OutputSlot> { new("InvoiceNo", "InvoiceNo", ["inv_num", "other"]), new("inv_num", "inv_num", ["inv_num", "other"]) };

        DraftBinder.ByMeaning(slots, fields).Should().ContainSingle().Which
            .Should().Be(new SlotMatch("InvoiceNo", "inv_num", 0.9f));

        // The leaf literally named like a field key matches by name at 1.0; the controller keeps that one and skips meaning for it.
        DraftBinder.ByName(slots, fields).Should().ContainSingle().Which.Path.Should().Be("inv_num");

        var twice = new FlowFieldList([.. fields.Fields, new FlowField("dup", "Dup", "text", FieldMeanings.DocumentNumber, false)]);
        DraftBinder.ByMeaning([new OutputSlot("InvoiceNo", "InvoiceNo", ["inv_num", "dup"])], twice).Should().BeEmpty();
    }
}
