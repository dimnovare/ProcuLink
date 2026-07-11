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
    // Per-org lock: DisableConcurrentExecution keys on the method + args, and this child takes
    // orgId as its argument, so two S3/R2 polls for the SAME org can never overlap. With the
    // claim-first ledger insert in S3IngressService this closes the concurrent-duplicate window.
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("S3PollOrgJob: starting S3/R2 poll for org {OrgId}.", orgId);

        var count = await _s3Ingress.PollAsync(orgId, ct);

        _logger.LogInformation("S3PollOrgJob: S3/R2 poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
