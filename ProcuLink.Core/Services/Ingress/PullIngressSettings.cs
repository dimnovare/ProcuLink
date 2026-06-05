namespace ProcuLink.Core.Services.Ingress;

// ── SFTP pull ─────────────────────────────────────────────────────────────────

public sealed record UpdateSftpIngressRequest(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string? Password,            // null = keep saved, "" = clear, value = replace
    string RemoteDirectory,
    Guid? DefaultSupplierId);

public sealed record SftpIngressResponse(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string RemoteDirectory,
    Guid? DefaultSupplierId,
    bool HasPassword,
    string? PasswordDisplay,     // "********" or null — never the ciphertext
    DateTime? UpdatedAt);

// ── S3 / R2 pull ──────────────────────────────────────────────────────────────

public sealed record UpdateS3IngressRequest(
    bool Enabled,
    string BucketName,
    string KeyPrefix,
    string Region,
    string AccessKeyId,
    string? SecretKey,           // null = keep saved, "" = clear, value = replace
    Guid? DefaultSupplierId,
    string? ServiceUrl = null);  // R2/MinIO endpoint; null/"" = standard AWS endpoint from region

public sealed record S3IngressResponse(
    bool Enabled,
    string BucketName,
    string KeyPrefix,
    string Region,
    string AccessKeyId,
    Guid? DefaultSupplierId,
    bool HasSecretKey,
    string? SecretKeyDisplay,    // "********" or null — never the ciphertext
    DateTime? UpdatedAt,
    string? ServiceUrl = null);  // R2/MinIO endpoint, or null for standard AWS

/// <summary>
/// Self-serve read/write of the per-org SFTP and S3/R2 pull-ingress configs.
/// One config per org per source (the first row is authoritative). Secrets are
/// encrypted via <c>DeliveryEncryptionService</c> and never returned to the client.
/// </summary>
public interface IPullIngressSettingsService
{
    Task<SftpIngressResponse> GetSftpAsync(Guid orgId, CancellationToken ct);
    Task<SftpIngressResponse> UpdateSftpAsync(Guid orgId, UpdateSftpIngressRequest request, CancellationToken ct);
    Task<S3IngressResponse> GetS3Async(Guid orgId, CancellationToken ct);
    Task<S3IngressResponse> UpdateS3Async(Guid orgId, UpdateS3IngressRequest request, CancellationToken ct);
}
