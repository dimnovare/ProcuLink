using FluentAssertions;
using ProcuLink.Core.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Security;

public class ApiKeyHasherTests
{
    [Fact]
    public void ComputeHash_SameInput_SameOutput()
    {
        var key = "plk_" + new string('a', 40);
        ApiKeyHasher.ComputeHash(key).Should().Be(ApiKeyHasher.ComputeHash(key));
    }

    [Fact]
    public void ComputeHash_DifferentInputs_DifferentOutputs()
    {
        var h1 = ApiKeyHasher.ComputeHash("plk_" + new string('x', 40));
        var h2 = ApiKeyHasher.ComputeHash("plk_" + new string('y', 40));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_ProducesLowercaseHex()
    {
        var hash = ApiKeyHasher.ComputeHash("plk_" + new string('a', 40));
        hash.Should().MatchRegex("^[0-9a-f]+$");
    }
}
