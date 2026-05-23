using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Storage;

/// <summary>
/// Cloudflare R2 implementation of <see cref="IFileStorageService"/>.
/// R2 is S3-compatible; we override the ServiceURL to the account-specific R2 endpoint.
/// Key convention:
///   source files  — {orgId}/{orderId}/{filename}
///   artifacts     — {orgId}/{orderId}/artifacts/{artifactId}.{ext}
/// </summary>
public sealed class R2StorageService : IFileStorageService, IAsyncDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucketName;

    public R2StorageService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Storage");

        var accessKeyId     = section["R2AccessKeyId"]     ?? throw new InvalidOperationException("Storage:R2AccessKeyId is not configured.");
        var secretAccessKey = section["R2SecretAccessKey"] ?? throw new InvalidOperationException("Storage:R2SecretAccessKey is not configured.");
        var endpoint        = section["R2Endpoint"]        ?? throw new InvalidOperationException("Storage:R2Endpoint is not configured.");
        _bucketName         = section["R2BucketName"]      ?? throw new InvalidOperationException("Storage:R2BucketName is not configured.");

        var config = new AmazonS3Config
        {
            ServiceURL            = endpoint,
            ForcePathStyle        = true,   // R2 requires path-style addressing
            AuthenticationRegion  = "auto"  // R2 uses "auto" as the signing region
        };

        _client = new AmazonS3Client(accessKeyId, secretAccessKey, config);
    }

    /// <inheritdoc/>
    public async Task<string> UploadAsync(
        Stream content, string key, string contentType, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName  = _bucketName,
            Key         = key,
            InputStream = content,
            ContentType = contentType,
            // Disable MD5 checksum — R2 does not require it and it causes issues with streaming bodies
            DisablePayloadSigning = false
        };

        await _client.PutObjectAsync(request, ct);
        return key;
    }

    /// <inheritdoc/>
    public Task<string> GetSignedDownloadUrlAsync(
        string key, TimeSpan expiry, CancellationToken ct)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key        = key,
            Verb       = HttpVerb.GET,
            Expires    = DateTime.UtcNow + expiry
        };

        // GetPreSignedURL is synchronous in AWSSDK.S3 v4
        var url = _client.GetPreSignedURL(request);
        return Task.FromResult(url);
    }

    /// <inheritdoc/>
    public async Task<Stream> DownloadAsync(string key, CancellationToken ct)
    {
        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key        = key,
        };

        var response = await _client.GetObjectAsync(request, ct);
        return response.ResponseStream;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var request = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key        = key
        };

        await _client.DeleteObjectAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await ValueTask.CompletedTask;
    }
}
