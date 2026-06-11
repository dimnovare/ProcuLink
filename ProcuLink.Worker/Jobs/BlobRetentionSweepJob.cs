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
/// (true = audit-only). Idempotent — purged blobs are flag-marked, so a re-run (or an
/// overlapping run after a missed save) matches nothing already purged, and storage deletes
/// are idempotent on a missing key. No automatic retry: the next daily run IS the retry.
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
