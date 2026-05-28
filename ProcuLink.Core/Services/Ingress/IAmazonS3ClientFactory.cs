using Amazon.S3;

namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Constructs an <see cref="IAmazonS3"/> bound to per-organisation credentials.
/// Introduced so <see cref="IS3IngressService"/> can authenticate against each
/// org's bucket using the decrypted credentials stored on its
/// <c>S3IngressConfig</c>, instead of a single shared platform client.
/// </summary>
public interface IAmazonS3ClientFactory
{
    /// <summary>
    /// Create a fresh <see cref="IAmazonS3"/> using the supplied credentials.
    /// The caller owns the returned instance.
    /// </summary>
    /// <param name="accessKeyId">AWS access key ID.</param>
    /// <param name="secretAccessKey">AWS secret access key (already decrypted).</param>
    /// <param name="region">AWS region identifier (e.g. <c>eu-west-1</c>, or <c>auto</c> for Cloudflare R2).</param>
    /// <param name="serviceUrl">Optional non-AWS endpoint (e.g. an R2 account URL). When null, the region is used to resolve the AWS endpoint.</param>
    IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl);
}
