using ProcuLink.Api.Auth;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

public class ClerkTokenValidationTests
{
    private static readonly IReadOnlySet<string> Authorized =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://localhost:8082",
            "https://proculink.eu",
        };

    [Fact]
    public void NullAzp_IsAccepted() =>
        Assert.True(ClerkTokenValidation.IsAuthorizedParty(null, Authorized));

    [Fact]
    public void EmptyAzp_IsAccepted() =>
        Assert.True(ClerkTokenValidation.IsAuthorizedParty(string.Empty, Authorized));

    [Fact]
    public void MatchingAzp_IsAccepted() =>
        Assert.True(ClerkTokenValidation.IsAuthorizedParty("https://proculink.eu", Authorized));

    [Fact]
    public void MatchingAzp_IsCaseInsensitive() =>
        Assert.True(ClerkTokenValidation.IsAuthorizedParty("HTTPS://ProcuLink.EU", Authorized));

    [Fact]
    public void ForeignAzp_IsRejected() =>
        Assert.False(ClerkTokenValidation.IsAuthorizedParty("https://evil-app.vercel.app", Authorized));
}
