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

/// <summary>PUT body. Password: null = keep stored, "" = clear, value = re-encrypt.</summary>
public sealed record UpsertCatalogSourceRequest(
    string Protocol,
    string Host,
    int Port,
    string? Username,
    string? Password,
    string RemotePath,
    string? FileFormat,
    int? SyncIntervalHours,
    bool IsEnabled);

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
    DateTime UpdatedAt);

/// <summary>Upsert outcome: the masked state + whether an immediate first sync was enqueued.</summary>
public sealed record CatalogSourceUpsertResult(CatalogSourceResponse Source, bool SyncEnqueued);
