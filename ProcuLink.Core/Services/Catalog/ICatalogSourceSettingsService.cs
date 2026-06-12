namespace ProcuLink.Core.Services.Catalog;

/// <summary>
/// CRUD for a supplier's catalog pull-source config. Secrets follow the
/// pull-ingress/delivery-config precedent: stored AES-GCM encrypted, responses masked
/// (never the ciphertext, never the plaintext), PUT password semantics
/// null = keep / "" = clear / value = re-encrypt.
/// </summary>
public interface ICatalogSourceSettingsService
{
    /// <summary>Returns the masked source config, or null when none is configured.</summary>
    Task<CatalogSourceResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct);

    /// <summary>
    /// Upserts the (single) source for the supplier. On an IsEnabled false→true transition
    /// a first sync is enqueued immediately (replacing the cut sync-now endpoint) UNLESS
    /// the M1-residual dedupe guard bites: no-op when the previous state is
    /// <c>LastSyncStatus == "running"</c> or <c>LastSyncAt</c> within the last 5 minutes.
    /// </summary>
    Task<CatalogSourceUpsertResult> UpsertAsync(
        Guid orgId, Guid supplierId, UpsertCatalogSourceRequest request, CancellationToken ct);

    /// <summary>Deletes the supplier's source. Returns false when none existed.</summary>
    Task<bool> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}

/// <summary>
/// PUT body. Secret semantics (both <see cref="Password"/> and <see cref="AuthConfig"/>):
/// null = keep stored, an empty/cleared value = clear, a value = re-encrypt.
///
/// For sftp/ftp/ftps the host/port/username/password/remote-path fields apply. For http/https
/// the <see cref="Url"/>, <see cref="AuthMethod"/>, <see cref="AuthConfig"/>, and
/// <see cref="HttpMethod"/> fields apply (host/port/remote-path are unused). Credentials MUST
/// NOT be embedded in the URL — <c>Uri.UserInfo</c> is rejected at save.
/// </summary>
public sealed record UpsertCatalogSourceRequest(
    string Protocol,
    string Host,
    int Port,
    string? Username,
    string? Password,
    string RemotePath,
    string? FileFormat,
    int? SyncIntervalHours,
    bool IsEnabled,
    // http/https only (null/ignored for sftp/ftp):
    string? Url = null,
    string? AuthMethod = null,
    CatalogHttpAuthConfig? AuthConfig = null,
    string? HttpMethod = null);

/// <summary>
/// HTTP auth secrets supplied on PUT (write-only). The exact field set used depends on
/// <see cref="UpsertCatalogSourceRequest.AuthMethod"/>:
///  • apikey → <see cref="ApiKeyHeader"/> + <see cref="ApiKeyValue"/>;
///  • bearer → <see cref="BearerToken"/>;
///  • basic  → <see cref="BasicUsername"/> + <see cref="BasicPassword"/>;
///  • oauth2_client_credentials → <see cref="TokenUrl"/> + <see cref="ClientId"/> +
///    <see cref="ClientSecret"/> + optional <see cref="Scope"/>.
/// Serialized to a normalized auth-config JSON, AES-GCM encrypted at rest, and never echoed back.
/// </summary>
public sealed record CatalogHttpAuthConfig(
    string? ApiKeyHeader = null,
    string? ApiKeyValue = null,
    string? BearerToken = null,
    string? BasicUsername = null,
    string? BasicPassword = null,
    string? TokenUrl = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Scope = null);

/// <summary>Masked GET/PUT response — never carries ciphertext or plaintext secrets.</summary>
public sealed record CatalogSourceResponse(
    Guid Id,
    string Protocol,
    string Host,
    int Port,
    string? Username,
    bool HasPassword,
    string? PasswordDisplay,
    string RemotePath,
    string FileFormat,
    int SyncIntervalHours,
    bool IsEnabled,
    DateTime? LastSyncAt,
    string? LastSyncStatus,
    string? LastSyncError,
    int? LastSyncCreated,
    int? LastSyncUpdated,
    int? LastSyncSkipped,
    DateTime UpdatedAt,
    // http/https only (null for sftp/ftp). Auth secrets are NEVER returned — only whether
    // they are present (HasAuthConfig) and the (non-secret) auth method.
    string? Url = null,
    string? AuthMethod = null,
    bool HasAuthConfig = false,
    string? HttpMethod = null);

/// <summary>Upsert outcome: the masked state + whether an immediate first sync was enqueued.</summary>
public sealed record CatalogSourceUpsertResult(CatalogSourceResponse Source, bool SyncEnqueued);
