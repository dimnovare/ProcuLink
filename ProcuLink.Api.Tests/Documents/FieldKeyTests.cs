using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class FieldKeyTests
{
    [Fact]
    public void A_key_at_the_pattern_limit_survives_normalising_and_stays_unique()
    {
        var key = new string('a', 64);
        FieldKey.Normalise(key).Should().Be(key);
        var unique = FieldKey.Unique(new HashSet<string> { key }, key);
        unique.Should().MatchRegex(FieldKey.Pattern.ToString()).And.EndWith("_2");
    }
}
