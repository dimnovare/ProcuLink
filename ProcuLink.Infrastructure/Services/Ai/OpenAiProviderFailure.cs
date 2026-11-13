using System.ClientModel;
using System.Net.Sockets;
using System.Security.Authentication;

namespace ProcuLink.Infrastructure.Services.Ai;

/// <summary>
/// Provider-failure classification for OpenAI calls. Moved out of the deleted
/// <c>OpenAiPdfOrderExtractor</c> (plan 13b, 2026-11-13) because <see cref="OpenAiDocumentReader"/>,
/// the flow document reader, decides between Warning and Error with it.
/// </summary>
internal static class OpenAiProviderFailure
{
    /// <summary>
    /// Names of the provider-level failures, used as a log field so an alert can group by cause.
    /// </summary>
    internal const string AuthFailure         = "auth";
    internal const string TransportFailure    = "transport";
    internal const string ProviderUnavailable = "provider_unavailable";

    /// <summary>
    /// Is this exception the AI PROVIDER failing, rather than this one document failing to read?
    /// Returns the failure's name when it is provider-level, and null when it is not.
    ///
    /// <para><b>Why it is deliberately narrow.</b> Error goes to Sentry, and an Error that fires on
    /// ordinary misses trains people to ignore Sentry. So a malformed response, an empty completion,
    /// a refusal, a schema mismatch and a 400 are all NOT provider failures: those are this document,
    /// this call. Only a failure that will repeat for the next document too counts.</para>
    ///
    /// <para><b>Not here on purpose:</b> HTTP 429. OpenAI returns it both for a transient burst
    /// limit and for an exhausted account quota; the two are told apart only by sniffing the
    /// response body, and this repo has exactly one sanctioned place for prose sniffing. Timeouts
    /// are excluded for the same reason: our own deadline fires on a large document as readily as
    /// on a sick provider, and it is caught a frame above this anyway.</para>
    /// </summary>
    internal static string? ClassifyProviderFailure(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException clientResult)
            {
                var kind = ClassifyProviderFailureStatus(clientResult.Status);
                if (kind is not null) return kind;
                continue;   // a 4xx we do not treat as provider-level; keep unwrapping
            }

            // No HTTP response at all: DNS, TLS, connection refused, a socket dropped mid-read.
            if (e is HttpRequestException or SocketException or AuthenticationException or IOException)
                return TransportFailure;
        }

        return null;
    }

    /// <summary>
    /// Maps an HTTP status from the provider onto a provider-level failure name, or null when the
    /// status describes this request rather than the provider (400 bad request, 404 unknown model,
    /// 422 — all of which repeat for this document and no other).
    /// </summary>
    internal static string? ClassifyProviderFailureStatus(int status) => status switch
    {
        // No status at all — the exception never carried a response, so nothing reached OpenAI.
        0            => TransportFailure,
        // A key that is missing, revoked, rotated out from under us, or not entitled to the model.
        401 or 403   => AuthFailure,
        // The provider itself is down, overloaded, or behind a broken gateway.
        >= 500       => ProviderUnavailable,
        _            => null,
    };
}
