using FluentAssertions;
using ProcuLink.Core.Security;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Security;

public class ApiKeyHasherTests
{
    private const string TestSecret = "test-server-secret-at-least-16-chars";

    [Fact]
    public void ComputeHash_SameInput_SameOutput()
    {
        var key = "plk_" + new string('a', 40);
        ApiKeyHasher.ComputeHash(key, TestSecret).Should().Be(ApiKeyHasher.ComputeHash(key, TestSecret));
    }

    [Fact]
    public void ComputeHash_DifferentInputs_DifferentOutputs()
    {
        var h1 = ApiKeyHasher.ComputeHash("plk_" + new string('x', 40), TestSecret);
        var h2 = ApiKeyHasher.ComputeHash("plk_" + new string('y', 40), TestSecret);
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_ProducesLowercaseHex()
    {
        var hash = ApiKeyHasher.ComputeHash("plk_" + new string('a', 40), TestSecret);
        hash.Should().MatchRegex("^[0-9a-f]+$");
    }

    /// <summary>
    /// Core security property: the same raw key hashed with two different server secrets
    /// must yield different hashes. This ensures that an attacker with read-only database
    /// access cannot recompute stored hashes without also knowing the server-side secret.
    /// </summary>
    [Fact]
    public void ComputeHash_DifferentServerSecrets_ProduceDifferentHashes()
    {
        var rawKey  = "plk_" + new string('a', 40);
        var secret1 = "server-secret-one-aaaa-bbbb-cccc-dddd";
        var secret2 = "server-secret-two-xxxx-yyyy-zzzz-wwww";

        var hash1 = ApiKeyHasher.ComputeHash(rawKey, secret1);
        var hash2 = ApiKeyHasher.ComputeHash(rawKey, secret2);

        hash1.Should().NotBe(hash2,
            "the same raw key keyed with different server secrets must produce different hashes; " +
            "if they matched, DB read access alone would be sufficient to forge authentication.");
    }
}
