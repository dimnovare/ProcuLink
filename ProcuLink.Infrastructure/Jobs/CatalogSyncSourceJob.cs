using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Catalog;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Per-source child job: runs one catalog pull through <see cref="ICatalogPullService"/>.
///
/// Idempotency: the sink is a natural-key upsert and the pull's hash-skip makes retries of
/// an unchanged file free, so Hangfire's 2 automatic retries are safe.
///
/// Soft lock: before pulling, the job stamps <c>LastSyncAt = utcnow</c> +
/// <c>LastSyncStatus = "running"</c> and saves — the next dispatcher window then sees the
/// source as not-due, preventing double dispatch. A crash mid-pull self-heals: the row
/// stays "running" until the interval elapses again and the next dispatch re-runs it.
///
/// Failure honesty (M4): the pull service throws an already-sanitized
/// <see cref="CatalogSyncException"/> (enumerated safe message, no inner exception). The
/// job persists <c>failed</c> + that safe message BEFORE rethrowing, so the UI shows the
/// truth even while Hangfire retries, and Sentry/dashboard only ever see sanitized text.
/// </summary>
public sealed class CatalogSyncSourceJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly ICatalogPullService _pull;
    private readonly ILogger<CatalogSyncSourceJob> _logger;

    public CatalogSyncSourceJob(
        ProcuLinkDbContext db,
        ICatalogPullService pull,
        ILogger<CatalogSyncSourceJob> logger)
    {
        _db = db;
        _pull = pull;
        _logger = logger;
    }

    [Queue("polling")]
    [AutomaticRetry(Attempts = 2)]
    public async Task ExecuteAsync(Guid orgId, Guid sourceId, CancellationToken ct)
    {
        // (1) Reload org-scoped; bail silently when deleted or disabled since enqueue.
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.OrgId == orgId, ct);
        if (source is null || !source.IsEnabled)
            return;

        // (2) Soft lock — the next dispatch sees not-due; crash self-heals at the next window.
        source.LastSyncAt = DateTime.UtcNow;
        source.LastSyncStatus = "running";
        source.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            // (3)+(4) Pull; the service persists the ok/unchanged outcome itself.
            var result = await _pull.PullAsync(orgId, sourceId, ct);
            _logger.LogInformation(
                "Catalog sync {Status} for source {SourceId} (org {OrgId}): created={Created} updated={Updated} skipped={Skipped}.",
                result.Status, sourceId, orgId, result.Created, result.Updated, result.Skipped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown — leave 'running'; the soft lock self-heals next window
        }
        catch (CatalogSyncException ex)
        {
            // (5) Persist failed + safe message BEFORE rethrowing the sanitized exception.
            await PersistFailureAsync(orgId, sourceId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: the pull service sanitizes everything, but never let an
            // unexpected raw exception reach Hangfire/Sentry from here either.
            _logger.LogDebug(ex, "Catalog sync failed unexpectedly for source {SourceId}.", sourceId);
            const string safe = "Catalog sync failed before the file could be imported.";
            await PersistFailureAsync(orgId, sourceId, safe);
            throw new CatalogSyncException(safe);
        }
    }

    private async Task PersistFailureAsync(Guid orgId, Guid sourceId, string safeMessage)
    {
        try
        {
            var source = await _db.SupplierCatalogSources
                .FirstOrDefaultAsync(s => s.Id == sourceId && s.OrgId == orgId);
            if (source is null) return;

            source.LastSyncStatus = "failed";
            source.LastSyncError = safeMessage.Length <= 500 ? safeMessage : safeMessage[..500];
            source.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception persistEx)
        {
            // Status bookkeeping must never mask the real failure being rethrown.
            _logger.LogWarning(persistEx,
                "Could not persist failed catalog-sync status for source {SourceId}.", sourceId);
        }
    }
}
