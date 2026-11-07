using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class SendWhenTests
{
    [Theory]
    [InlineData("is", "Acme", " acme ", true)]
    [InlineData("is", "Acme", "Acme Ltd", false)]
    [InlineData("is_not", "Acme", null, true)]
    [InlineData("contains", "acme", "ACME Ltd", true)]
    [InlineData("contains", "Acme", null, false)]
    [InlineData("nope", "Acme", "Acme", false)]
    public void Matches_ComparesTrimmedAndCaseInsensitive(string op, string wanted, string? actual, bool expected)
    {
        var header = actual is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["supplier"] = actual };

        SendWhenOps.Matches(new SendWhen("supplier", op, wanted), header).Should().Be(expected);
        SendWhenOps.Matches(null, header).Should().BeTrue();
    }
}
