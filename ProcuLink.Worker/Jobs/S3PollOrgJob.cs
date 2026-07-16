using Hangfire;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Per-organisation child job: polls the S3/R2 bucket and imports new purchase-order
/// objects for one organisation. Scheduled by <see cref="S3PollingJob"/>.
/// The underlying <see cref="IS3IngressService.PollAsync"/> is idempotent (tracks
/// seen object keys), so re-running after a crash safely skips already-imported objects.
/// </summary>
public sealed class S3PollOrgJob
{
    private readonly IS3IngressService _s3Ingress;
    private readonly ILogger<S3PollOrgJob> _logger;

    public S3PollOrgJob(IS3IngressService s3Ingress, ILogger<S3PollOrgJob> logger)
    {
        _s3Ingress = s3Ingress;
        _logger = logger;
    }

    [Queue("polling")]
    [AutomaticRetry(Attempts = 2)]
    // PER-ORG lock: PerOrgDistributedMutex acquires a storage-backed distributed lock keyed on the
    // orgId ARGUMENT (resource "poll:S3PollOrgJob.ExecuteAsync:{orgId}"), so two S3/R2 polls for the
    // SAME org can never overlap, while two DIFFERENT orgs never contend and poll in parallel.
    // NOT [DisableConcurrentExecution]: on OSS Hangfire that keys on job TYPE + METHOD ONLY (never
    // the arguments), which serialised EVERY org's S3/R2 polling through one lock — one slow or hung
    // tenant bucket stalled all other tenants for up to the timeout.
    // Per-org granularity is sufficient because the lock is only the OUTER guard: the claim-first
    // ledger insert + unique index in S3IngressService is what actually guarantees exactly-one
    // order, and two different orgs share no state to race over.
    [PerOrgDistributedMutex(orgArgumentIndex: 0, timeoutSeconds: 300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("S3PollOrgJob: starting S3/R2 poll for org {OrgId}.", orgId);

        var count = await _s3Ingress.PollAsync(orgId, ct);

        _logger.LogInformation("S3PollOrgJob: S3/R2 poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
