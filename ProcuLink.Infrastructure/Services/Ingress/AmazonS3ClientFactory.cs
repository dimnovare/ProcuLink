using Amazon.S3;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Production implementation of <see cref="IAmazonS3ClientFactory"/> that
/// returns a freshly-constructed <see cref="AmazonS3Client"/> for each call.
/// Stateless and safe to register as a singleton.
/// </summary>
public sealed class AmazonS3ClientFactory : IAmazonS3ClientFactory
{
    /// <inheritdoc />
    public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl)
    {
        var config = new AmazonS3Config
        {
            AuthenticationRegion = region,
            ForcePathStyle       = true,
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
