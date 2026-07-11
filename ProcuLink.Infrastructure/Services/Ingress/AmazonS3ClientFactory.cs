using Amazon.S3;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production implementation of <see cref="IAmazonS3ClientFactory"/> that
/// returns a freshly-constructed <see cref="AmazonS3Client"/> for each call.
/// Stateless and safe to register as a singleton.
///
/// <para>
/// Every client is wired to a <see cref="GuardedAwsHttpClientFactory"/> so all S3/R2 traffic
/// flows through the <see cref="OutboundRequestGuard"/>'s connect-time-revalidating transport.
/// This closes the DNS-rebinding TOCTOU window (audit finding #6): the up-front
/// <see cref="OutboundRequestGuard.ValidateAsync"/> on a tenant <c>ServiceUrl</c> resolves DNS
/// once, but the AWS SDK re-resolves at connect — pinning the validated IP at connect time
/// blocks a public→private/metadata rebind, matching the HTTP delivery path.
/// </para>
/// </summary>
public sealed class AmazonS3ClientFactory : IAmazonS3ClientFactory
{
    // One shared guarded transport for all orgs (the HttpClient it builds is a stateless,
    // connection-pooling transport; per-request signing/credentials are applied by the SDK).
    private readonly GuardedAwsHttpClientFactory _guardedHttpClientFactory;

    public AmazonS3ClientFactory(OutboundRequestGuard guard)
    {
        _guardedHttpClientFactory = new GuardedAwsHttpClientFactory(guard);
    }

    /// <inheritdoc />
    public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl)
    {
        var config = new AmazonS3Config
        {
            AuthenticationRegion = region,
            ForcePathStyle       = true,
            // SSRF connect-time re-validation + IP-pinning for the data plane (finding #6).
            HttpClientFactory    = _guardedHttpClientFactory,
        };

        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        return new AmazonS3Client(accessKeyId, secretAccessKey, config);
    }
}
