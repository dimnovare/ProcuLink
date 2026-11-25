using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Output.Ksef;

public class PolishNipTests
{
    [Theory]
    [InlineData("1111111111", true)]
    [InlineData("PL 999-999-99-99", true)]
    [InlineData("1111111112", false)]
    [InlineData("123", false)]
    [InlineData(null, false)]
    public void PolishNip_checks_the_checksum(string? raw, bool valid) => PolishNip.IsValid(raw).Should().Be(valid);
}
