using Amazon.S3;

namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Constructs an <see cref="IAmazonS3"/> bound to per-organisation credentials.
/// Lets the S3 pickup source and the S3 delivery dispatcher authenticate against each
/// org's bucket with its own decrypted credentials, instead of a single shared platform client.
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
