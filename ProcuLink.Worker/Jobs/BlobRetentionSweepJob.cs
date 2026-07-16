using Hangfire;
using ProcuLink.Core.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (daily): thin wrapper over <see cref="IBlobRetentionService"/> so the
/// sweep logic stays unit-tested in the Infrastructure suite (mirrors
/// <see cref="DataRetentionSweepJob"/>). Purges ONLY R2 file blobs (source + artifacts) of
/// TERMINAL orders older than each opted-in org's <c>retention_days</c> window; DB rows,
/// hashes, provenance and audit trail always stay. TWO safety latches, both default OFF:
/// per-org <c>retention_days</c> (NULL = disabled) and global <c>Retention:DryRun</c>
/// (true = audit-only). Idempotent — purged blobs are flag-marked, so a SEQUENTIAL re-run
/// matches nothing already purged. A genuinely OVERLAPPING run is not excluded by that flag
/// (both runs select before either marks), and is deliberately left unguarded: every racing
/// effect is benign — storage deletes are idempotent on a missing key and the purge flag is a
/// constant write, not an increment, so last-write-wins loses nothing. Worst case is one audit
/// row's FilesDeleted stat double-counting a blob. No automatic retry: the next daily run IS
/// the retry.
/// </summary>
public sealed class BlobRetentionSweepJob
{
    private readonly IBlobRetentionService _service;
    private readonly ILogger<BlobRetentionSweepJob> _logger;

    public BlobRetentionSweepJob(
        IBlobRetentionService service,
        ILogger<BlobRetentionSweepJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var result = await _service.RunAsync(ct);
        if (result.Organisations.Count == 0)
        {
            _logger.LogInformation("BlobRetentionSweepJob: no org has retention enabled — nothing to do.");
            return;
        }

        foreach (var org in result.Organisations)
            _logger.LogInformation(
                "BlobRetentionSweepJob: org {OrgId} mode={Mode} orders={Orders} sourceBlobs={Source} artifactBlobs={Artifacts} failures={Failures} ~bytes={Bytes}.",
                org.OrgId, org.Mode, org.OrdersExamined, org.SourceFilesPurged,
                org.ArtifactBlobsPurged, org.DeleteFailures, org.BytesEstimated);
    }
}
