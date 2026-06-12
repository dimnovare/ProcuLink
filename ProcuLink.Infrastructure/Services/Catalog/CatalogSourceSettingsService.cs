using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Infrastructure.Services.Catalog;

/// <summary>
/// CRUD for supplier catalog pull sources. Mirrors <see cref="PullIngressSettingsService"/>:
/// secrets are encrypted via <see cref="DeliveryEncryptionService"/> (AES-256-GCM,
/// write-only) and responses are masked. The enable-transition enqueue replaces the cut
/// sync-now endpoint, guarded against flooding by the M1-residual dedupe rule.
/// </summary>
public sealed class CatalogSourceSettingsService : ICatalogSourceSettingsService
{
    private const string Mask = "********";

    /// <summary>M1 residual: don't enqueue an enable-transition sync when one ran this recently.</summary>
    private static readonly TimeSpan EnqueueDedupeWindow = TimeSpan.FromMinutes(5);

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IBackgroundJobClient _jobs;

    public CatalogSourceSettingsService(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _encryption = encryption;
        _jobs = jobs;
    }

    public async Task<CatalogSourceResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);

        return source is null ? null : ToResponse(source);
    }

    public async Task<CatalogSourceUpsertResult> UpsertAsync(
        Guid orgId, Guid supplierId, UpsertCatalogSourceRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);

        // Captured BEFORE mutation — drives the false→true enable-transition detection
        // and the M1-residual dedupe guard.
        var wasEnabled      = source?.IsEnabled ?? false;
        var priorStatus     = source?.LastSyncStatus;
        var priorLastSyncAt = source?.LastSyncAt;

        if (source is null)
        {
            source = new SupplierCatalogSource
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                CreatedAt = now,
            };
            _db.SupplierCatalogSources.Add(source);
        }

        var protocol = request.Protocol.Trim().ToLowerInvariant();
        source.Protocol = protocol;
        source.Host = request.Host.Trim();
        source.Port = request.Port > 0 ? request.Port : (protocol == "sftp" ? 22 : 21);
        source.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        source.RemotePath = request.RemotePath.Trim();
        source.FileFormat = string.IsNullOrWhiteSpace(request.FileFormat) ? "auto" : request.FileFormat.Trim().ToLowerInvariant();
        source.SyncIntervalHours = Math.Clamp(request.SyncIntervalHours ?? source.SyncIntervalHours, 1, 336);
        source.IsEnabled = request.IsEnabled;

        // Write-only secret semantics (precedent PullIngressSettingsService): null = keep,
        // "" = clear, value = re-encrypt. Plaintext is never stored or echoed back.
        if (request.Password is not null)
        {
            source.EncryptedPassword = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : _encryption.Encrypt(request.Password);
        }

        source.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // Enable-transition enqueue (replaces sync-now): only on false→true, and no-op when
        // a sync is already running or one happened within the dedupe window (M1 residual).
        var enqueued = false;
        if (request.IsEnabled && !wasEnabled)
        {
            var recentlySynced = priorLastSyncAt is not null && now - priorLastSyncAt.Value < EnqueueDedupeWindow;
            if (priorStatus != "running" && !recentlySynced)
            {
                var sourceId = source.Id;
                _jobs.Enqueue<CatalogSyncSourceJob>(j => j.ExecuteAsync(orgId, sourceId, CancellationToken.None));
                enqueued = true;
            }
        }

        return new CatalogSourceUpsertResult(ToResponse(source), enqueued);
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);
        if (source is null) return false;

        _db.SupplierCatalogSources.Remove(source);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static CatalogSourceResponse ToResponse(SupplierCatalogSource s)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(s.EncryptedPassword);
        return new CatalogSourceResponse(
            s.Id, s.Protocol, s.Host, s.Port, s.Username,
            hasPassword, hasPassword ? Mask : null,
            s.RemotePath, s.FileFormat, s.SyncIntervalHours, s.IsEnabled,
            s.LastSyncAt, s.LastSyncStatus, s.LastSyncError,
            s.LastSyncCreated, s.LastSyncUpdated, s.LastSyncSkipped,
            s.UpdatedAt);
    }
}
