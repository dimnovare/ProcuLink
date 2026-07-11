using Amazon.Runtime;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// An AWS SDK <see cref="HttpClientFactory"/> that routes every S3/R2 request through the
/// <see cref="OutboundRequestGuard"/>'s connect-time-revalidating transport
/// (<see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/>).
///
/// <para>
/// Why this exists (audit finding #6 — DNS-rebinding TOCTOU SSRF): the up-front
/// <see cref="OutboundRequestGuard.ValidateAsync"/> on a tenant-supplied S3 <c>ServiceUrl</c>
/// resolves + validates DNS once, but the AWS SDK then RE-RESOLVES the host at TCP connect.
/// A low-TTL malicious DNS server can answer with a public IP during validation and with
/// <c>169.254.169.254</c> (cloud metadata) / an RFC-1918 address a moment later at connect.
/// Setting this factory on <c>AmazonS3Config.HttpClientFactory</c> makes the SDK send through a
/// <see cref="System.Net.Http.SocketsHttpHandler"/> whose <c>ConnectCallback</c> re-resolves and
/// re-validates the destination and connects to the pinned validated IP — the same defence the
/// HTTP delivery dispatcher and integration-trigger job already use. This is the S3-over-HTTP
/// path, the highest-impact vector because the metadata endpoint is HTTP-reachable.
/// </para>
///
/// <para>
/// A single guarded <see cref="System.Net.Http.HttpClient"/> is built lazily and shared across
/// every request and every org. This is safe: the HttpClient is a stateless transport
/// (per-request AWS SigV4 signing and credentials are applied by the SDK, not the client), the
/// underlying <c>SocketsHttpHandler</c> pools connections, and the <c>ConnectCallback</c> pins
/// the destination per request from the request URI. Because we own the shared client's
/// lifetime we opt out of the SDK's own caching/disposal so it is never disposed out from under
/// a concurrent request.
/// </para>
/// </summary>
internal sealed class GuardedAwsHttpClientFactory : HttpClientFactory
{
    private readonly OutboundRequestGuard _guard;
    private readonly object _lock = new();
    private System.Net.Http.HttpClient? _client;

    public GuardedAwsHttpClientFactory(OutboundRequestGuard guard)
    {
        _guard = guard;
    }

    /// <summary>
    /// Returns the shared guarded <see cref="System.Net.Http.HttpClient"/>, building it on first
    /// use from the guard's connect-time-revalidating handler. AWS recommends an infinite
    /// <c>HttpClient.Timeout</c> for injected clients and relying on the SDK's per-request
    /// timeout (<c>IClientConfig.Timeout</c>, enforced via the request cancellation token), so a
    /// single shared client with one timeout does not fight the per-org configured timeout.
    /// </summary>
    public override System.Net.Http.HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        if (_client is not null) return _client;

        lock (_lock)
        {
            _client ??= new System.Net.Http.HttpClient(_guard.CreateGuardedHttpHandler(), disposeHandler: true)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        return _client;
    }

    // We own the single shared client's lifetime — keep it out of the SDK's static cache and
    // never let the SDK dispose it (a shared, connection-pooling transport must outlive any one
    // request or AmazonS3Client instance).
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
}
