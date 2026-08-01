namespace ProcuLink.Core.Services.Ingress;

// ── SFTP pull ─────────────────────────────────────────────────────────────────

public sealed record UpdateSftpIngressRequest(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string? Password,            // null = keep saved, "" = clear, value = replace
    string RemoteDirectory,
    Guid? DefaultSupplierId,
    // Trusted SSH host-key fingerprint(s), newline-separated, in OpenSSH's "SHA256:…" form. Same
    // null = keep / "" = clear / value = replace semantics as Password — but for the OPPOSITE reason:
    // this one is not a secret, it is returned in full by the GET, and clearing it is the deliberate
    // re-trust after a supplier legitimately rebuilds their server. The next poll then records
    // whatever it finds and pins to that.
    string? HostKeyFingerprints = null);

public sealed record SftpIngressResponse(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string RemoteDirectory,
    Guid? DefaultSupplierId,
    bool HasPassword,
    string? PasswordDisplay,     // "********" or null — never the ciphertext
    DateTime? UpdatedAt,
    // Returned IN FULL, deliberately: an operator asked to decide whether a changed host key is a
    // supplier rebuild or an interception cannot decide it without seeing the value they are
    // comparing against. Null until the first successful poll records one.
    string? HostKeyFingerprints = null);

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
