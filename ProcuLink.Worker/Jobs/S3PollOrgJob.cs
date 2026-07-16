using Hangfire;
using ProcuLink.Core.Services.Ingress;

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
    // GLOBAL lock, not per-org: on OSS Hangfire, DisableConcurrentExecution keys the distributed
    // lock on job TYPE + METHOD ONLY — it does NOT include this job's orgId argument. Per-argument
    // mutexing needs the paid Hangfire.Pro [Mutex] (see PerOrderDistributedMutexAttribute, which
    // exists for exactly that reason). A global lock strictly CONTAINS the per-org lock the
    // claim-first ledger insert in S3IngressService needs, so correctness is unaffected — two
    // S3/R2 polls for the SAME org still can never overlap. The cost is throughput: every org's
    // S3/R2 polling serialises through this one lock, a ceiling as org count grows. Tracked
    // separately.
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("S3PollOrgJob: starting S3/R2 poll for org {OrgId}.", orgId);

        var count = await _s3Ingress.PollAsync(orgId, ct);

        _logger.LogInformation("S3PollOrgJob: S3/R2 poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
