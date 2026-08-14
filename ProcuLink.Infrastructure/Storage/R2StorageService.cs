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
            // R2 does not support STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER.
            // DisablePayloadSigning=true switches to a standard (non-chunked) PUT
            // which R2 accepts correctly.
            DisablePayloadSigning = true,
            UseChunkEncoding      = false,
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
        // Use a direct signed GetObject rather than a pre-signed URL.
        //
        // A pre-signed URL bakes X-Amz-Date / X-Amz-Expires into the signature at
        // generation time using the local clock. On hosts whose clock drifts (observed
        // on the Railway Worker container) R2 returns 403 SignatureDoesNotMatch because
        // the offline-signed URL can never self-correct. GetObjectAsync signs each
        // request live and the SDK auto-corrects clock skew from R2's Date response
        // header, so it works where the pre-signed URL 403s. This matches what ran
        // reliably in production before the pre-signed download was introduced.
        var response = await _client.GetObjectAsync(
            new GetObjectRequest { BucketName = _bucketName, Key = key }, ct);

        // Copy into a MemoryStream so the S3 response/connection can be disposed safely.
        var ms = new MemoryStream();
        await using (response.ResponseStream)
        {
            await response.ResponseStream.CopyToAsync(ms, ct);
        }
        ms.Position = 0;
        return ms;
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

    /// <inheritdoc/>
    public async Task<long?> TryGetSizeAsync(string key, CancellationToken ct)
    {
        // Same client/auth as every other call; HEAD-equivalent metadata request.
        // Best-effort by contract: any failure (missing key, transient error) → null.
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucketName, Key = key }, ct);
            return metadata.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A HEAD (GetObjectMetadata) against a key that is not expected to exist. This is the cheapest
    /// call that actually LEAVES THE PROCESS: it resolves <c>ServiceURL</c>, opens TLS, and gets the
    /// request signature checked by R2.
    ///
    /// <para>A 404 is the SUCCESS case and the whole trick — being told "no such key" can only come
    /// from a reachable endpoint that accepted our credentials and found the bucket. Contrast the
    /// pre-signed-URL check this replaced, which produced a perfect-looking URL against a
    /// misconfigured endpoint because signing never leaves the machine.</para>
    ///
    /// <para>A 403/401 is the failure this exists to catch: reachable host, rejected credentials
    /// (or a bucket the key cannot see) — an upload would 403 for the same reason.</para>
    /// </remarks>
    public async Task<StorageProbe> ProbeAsync(CancellationToken ct)
    {
        const string probeKey = "__healthcheck__/probe";
        try
        {
            await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucketName, Key = probeKey }, ct);

            // The probe key exists. Unexpected, but it still answers the only question asked.
            return StorageProbe.Reachable("Storage answered a HEAD request for the probe key.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The point of the probe: a live, authenticated "no such key".
            return StorageProbe.Reachable(
                "Storage answered a HEAD request (404 for the probe key, which is expected).");
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return StorageProbe.Unreachable(
                $"Storage rejected our credentials ({(int)ex.StatusCode} {ex.ErrorCode}). " +
                "Uploads and downloads will fail for the same reason.");
        }
        catch (AmazonS3Exception ex)
        {
            return StorageProbe.Unreachable(
                $"Storage returned {(int)ex.StatusCode} {ex.ErrorCode} for a HEAD request.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // DNS failure, connect refused, TLS error, probe timeout — a wrong ServiceURL lands here.
            return StorageProbe.Unreachable(
                $"Storage did not answer a HEAD request: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await ValueTask.CompletedTask;
    }
}
