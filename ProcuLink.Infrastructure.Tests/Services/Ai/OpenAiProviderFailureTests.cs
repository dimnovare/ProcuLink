using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// An AI provider outage must not be silent.
///
/// <para>The classifier decides whether a failed OpenAI call is logged at Error (the provider) or
/// Warning (this document). The Worker's Sentry integration captures at Error and above, so logging
/// at Error IS the alert. An Error that fires on ordinary per-document misses trains people to
/// ignore Sentry, so these tests pin BOTH directions — a 503 is an Error, and a 400 is not.</para>
///
/// <para>Re-anchored 2026-11-13 (plan 13b): the classifier moved from the deleted
/// <c>OpenAiPdfOrderExtractor</c> to <see cref="OpenAiProviderFailure"/>, which the flow
/// document reader uses. The extractor's own logging-wiring facts were deleted with it.</para>
/// </summary>
public class OpenAiProviderFailureTests
{
    // ── The classifier, both directions ──────────────────────────────────────

    [Theory]
    [InlineData(0)]     // the exception carried no response at all
    [InlineData(401)]   // key missing, revoked, or rotated out from under us
    [InlineData(403)]   // key not entitled to this model
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(529)]
    public void A_provider_level_status_is_classified_as_a_provider_failure(int status)
    {
        OpenAiProviderFailure.ClassifyProviderFailureStatus(status)
            .Should().NotBeNull($"HTTP {status} will repeat for the next document too");
    }

    [Theory]
    [InlineData(400)]   // this request's schema/prompt
    [InlineData(404)]   // unknown model — a config error, not an outage
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(429)]   // deliberately excluded: a transient burst limit reads identically to an
                        // exhausted quota without sniffing the body for insufficient_quota
    public void A_request_level_status_is_not_a_provider_failure(int status)
    {
        OpenAiProviderFailure.ClassifyProviderFailureStatus(status)
            .Should().BeNull($"HTTP {status} describes this call, and an Error on it would be noise");
    }

    [Fact]
    public void An_auth_status_and_an_outage_status_are_told_apart()
    {
        // Not a constant compared to itself: the point is that the two causes carry DIFFERENT
        // names, so an alert can group by cause rather than seeing one undifferentiated blob.
        var auth   = OpenAiProviderFailure.ClassifyProviderFailureStatus(401);
        var outage = OpenAiProviderFailure.ClassifyProviderFailureStatus(503);

        auth.Should().NotBe(outage);
        auth.Should().Be(OpenAiProviderFailure.AuthFailure);
        outage.Should().Be(OpenAiProviderFailure.ProviderUnavailable);
    }

    [Fact]
    public void A_transport_exception_is_a_provider_failure_however_deeply_it_is_wrapped()
    {
        OpenAiProviderFailure.ClassifyProviderFailure(new HttpRequestException("connection refused"))
            .Should().Be(OpenAiProviderFailure.TransportFailure);

        OpenAiProviderFailure.ClassifyProviderFailure(new SocketException(10061))
            .Should().Be(OpenAiProviderFailure.TransportFailure);

        OpenAiProviderFailure.ClassifyProviderFailure(new AuthenticationException("TLS handshake failed"))
            .Should().Be(OpenAiProviderFailure.TransportFailure);

        // The SDK wraps; the classifier unwraps.
        OpenAiProviderFailure.ClassifyProviderFailure(
            new InvalidOperationException("outer", new HttpRequestException("DNS failure")))
            .Should().Be(OpenAiProviderFailure.TransportFailure);
    }

    [Fact]
    public void An_ordinary_extraction_miss_is_not_a_provider_failure()
    {
        OpenAiProviderFailure.ClassifyProviderFailure(null).Should().BeNull();

        OpenAiProviderFailure.ClassifyProviderFailure(new JsonException("unexpected token"))
            .Should().BeNull("a malformed model response is this document, not the provider");

        OpenAiProviderFailure.ClassifyProviderFailure(new InvalidOperationException("no content"))
            .Should().BeNull();

        OpenAiProviderFailure.ClassifyProviderFailure(new NotSupportedException())
            .Should().BeNull();
    }
}
