using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// S3 or S3-compatible bucket delivery (AWS S3, Cloudflare R2, MinIO, Wasabi). Puts the artifact at
/// <c>prefix + fileName</c>; a 2xx from PutObject is "accepted". Never throws. The key is a
/// deterministic function of the delivery, so a re-send overwrites its own object (ResendSafety.Safe).
/// </summary>
public sealed class S3DeliveryDispatcher(
    IAmazonS3ClientFactory factory, OutboundRequestGuard guard, ILogger<S3DeliveryDispatcher> logger) : IDeliveryDispatcher
{
    // Errors no retry can fix: wrong or revoked keys, wrong bucket.
    private static readonly string[] PermanentCodes =
        ["AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch", "NoSuchBucket", "AllAccessDisabled", "InvalidBucketName"];

    public string Protocol => DeliveryProtocolConstants.S3;
    public ResendSafety ResendSafety => ResendSafety.Safe;
    public bool CapturesSupplierResponseBody => false;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        try
        {
            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            var bucket = Text(cfg, "bucket");
            var region = Text(cfg, "region");
            var accessKeyId = Text(cfg, "accessKeyId");
            var serviceUrl = Text(cfg, "serviceUrl");
            var secret = "";
            if (!string.IsNullOrEmpty(decryptedCredentials) && JsonNode.Parse(decryptedCredentials) is JsonObject creds)
                secret = Text(creds, "secretKey") ?? "";

            if (bucket is null || region is null || accessKeyId is null)
                return Permanent("S3 delivery configuration is invalid: bucket, region and access key ID are required.");
            if (secret.Length == 0)
                return Permanent("S3 delivery credentials are missing: the secret access key is required.");

            if (serviceUrl is not null)
            {
                var verdict = OutboundUrlPolicy.Inspect(serviceUrl, "Endpoint URL");
                if (!verdict.Allowed) return Permanent($"S3 delivery blocked: {verdict.Message}");
                var guarded = await guard.ValidateAsync(serviceUrl, ct);
                // A DNS blip also lands here, so it stays retryable like the HTTP/SFTP dispatchers.
                if (!guarded.Allowed) return new DeliveryResult(false, $"S3 delivery blocked: {guarded.Reason}");
            }

            using var s3 = factory.Create(accessKeyId, secret, region, serviceUrl);
            using var stream = new MemoryStream(content, writable: false);
            // Bounded like SFTP; the caller's own cancellation still propagates (filter below).
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(SftpDeliveryDispatcher.DefaultTimeoutSeconds));
            var response = await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = NormalisePrefix(Text(cfg, "prefix")) + LastSegment(fileName),
                InputStream = stream,
                ContentType = contentType,
                // R2 rejects aws-chunked uploads with a trailing checksum (see R2StorageService).
                UseChunkEncoding = false,
                DisablePayloadSigning = serviceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? true,
            }, deadline.Token);
            var code = (int)response.HttpStatusCode;
            return code is >= 200 and < 300
                ? new DeliveryResult(true, null, code)
                : new DeliveryResult(false, $"The bucket answered HTTP {code}.", code);
        }
        catch (AmazonS3Exception ex)
        {
            var status = (int)ex.StatusCode;
            // Wrong region (301, AuthorizationHeaderMalformed) and other 4xx are configuration faults;
            // 408, 429, SlowDown and 5xx are worth retrying.
            var permanent = PermanentCodes.Contains(ex.ErrorCode)
                || (status is >= 300 and < 500 && status is not (408 or 429) && ex.ErrorCode is not ("RequestTimeout" or "SlowDown"));
            logger.LogWarning("S3 delivery refused: {Code} (HTTP {Status})", ex.ErrorCode, status);
            var label = ex.ErrorCode ?? "HTTP " + status;
            return new DeliveryResult(false,
                permanent
                    ? $"The bucket refused the upload ({label}). Check the bucket name, region, access key and secret."
                    : $"The bucket is not accepting uploads right now ({label}).",
                status, Outcome: permanent ? DeliveryOutcome.NotRetryable : DeliveryOutcome.Dispatched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeouts, DNS and connection faults: transient.
            logger.LogWarning(ex, "S3 delivery failed");
            return new DeliveryResult(false, "The bucket could not be reached.");
        }
    }

    private static DeliveryResult Permanent(string message) => new(false, message, Outcome: DeliveryOutcome.NotRetryable);

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>No leading slash, no empty / "." / ".." segments; non-empty ends in one slash.</summary>
    internal static string NormalisePrefix(string? prefix)
    {
        var parts = (prefix ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not ("." or ".."));
        var joined = string.Join('/', parts);
        return joined.Length == 0 ? "" : joined + "/";
    }

    private static string LastSegment(string fileName) => fileName.Replace('\\', '/').Split('/').Last();
}
