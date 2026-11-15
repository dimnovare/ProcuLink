using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class ReadLimitsTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(10, 1)] [InlineData(11, 2)]
    [InlineData(100, 10)] [InlineData(101, 10)] [InlineData(2500, 10)]
    public void A_file_counts_one_unit_per_ten_pages_up_to_ten(int pages, int units) =>
        ReadLimits.UnitsFor(pages).Should().Be(units);
}
