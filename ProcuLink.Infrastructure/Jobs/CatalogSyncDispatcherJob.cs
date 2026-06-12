using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hourly recurring dispatcher: finds every enabled catalog source that is DUE
/// (never synced, or last synced at least its interval ago) and enqueues one
/// <see cref="CatalogSyncSourceJob"/> child per source — per-supplier isolation, so one
/// slow or hung server cannot block other suppliers' syncs (SftpPollingJob precedent).
///
/// Due filtering runs in memory over a projected list: the table holds at most one row
/// per (org, supplier) — dozens of rows for years (scope review) — and this avoids
/// per-row interval arithmetic in SQL. The child job's soft lock (it stamps
/// <c>LastSyncAt</c>/<c>running</c> before pulling) makes a source not-due for the next
/// dispatch window, so overlapping dispatches do not double-sync.
/// </summary>
public sealed class CatalogSyncDispatcherJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<CatalogSyncDispatcherJob> _logger;

    public CatalogSyncDispatcherJob(
        ProcuLinkDbContext db,
        IBackgroundJobClient jobs,
        ILogger<CatalogSyncDispatcherJob> logger)
    {
        _db = db;
        _jobs = jobs;
        _logger = logger;
    }

    [Queue("polling")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var enabled = await _db.SupplierCatalogSources
            .AsNoTracking()
            .Where(s => s.IsEnabled)
            .Select(s => new { s.Id, s.OrgId, s.LastSyncAt, s.SyncIntervalHours })
            .ToListAsync(ct);

        var due = enabled
            .Where(s => IsDue(s.LastSyncAt, s.SyncIntervalHours, now))
            .ToList();

        foreach (var s in due)
            _jobs.Enqueue<CatalogSyncSourceJob>(j => j.ExecuteAsync(s.OrgId, s.Id, CancellationToken.None));

        _logger.LogInformation(
            "Catalog sync dispatcher: {Enabled} enabled source(s), {Due} due — child job(s) enqueued.",
            enabled.Count, due.Count);
    }

    /// <summary>Due = never synced, or the (clamped) interval has fully elapsed.</summary>
    internal static bool IsDue(DateTime? lastSyncAt, int syncIntervalHours, DateTime utcNow)
    {
        if (lastSyncAt is null) return true;
        var interval = Math.Clamp(syncIntervalHours, 1, 336);
        return lastSyncAt.Value <= utcNow.AddHours(-interval);
    }
}
