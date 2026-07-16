using Hangfire;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Per-organisation child job: opens the SFTP connection and imports new purchase-order
/// files for one organisation. Scheduled by <see cref="SftpPollingJob"/>.
/// The underlying <see cref="ISftpIngressService.PollAsync"/> is idempotent (tracks
/// seen files), so re-running after a crash safely skips already-imported files.
/// </summary>
public sealed class SftpPollOrgJob
{
    private readonly ISftpIngressService _sftpIngress;
    private readonly ILogger<SftpPollOrgJob> _logger;

    public SftpPollOrgJob(ISftpIngressService sftpIngress, ILogger<SftpPollOrgJob> logger)
    {
        _sftpIngress = sftpIngress;
        _logger = logger;
    }

    [Queue("polling")]
    [AutomaticRetry(Attempts = 2)]
    // GLOBAL lock, not per-org: on OSS Hangfire, DisableConcurrentExecution keys the distributed
    // lock on job TYPE + METHOD ONLY — it does NOT include this job's orgId argument. Per-argument
    // mutexing needs the paid Hangfire.Pro [Mutex] (see PerOrderDistributedMutexAttribute, which
    // exists for exactly that reason). A global lock strictly CONTAINS the per-org lock the
    // claim-first ledger insert in SftpIngressService needs, so correctness is unaffected — two
    // SFTP polls for the SAME org still can never overlap. The cost is throughput: every org's
    // SFTP polling serialises through this one lock, a ceiling as org count grows. Tracked
    // separately.
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("SftpPollOrgJob: starting SFTP poll for org {OrgId}.", orgId);

        var count = await _sftpIngress.PollAsync(orgId, ct);

        _logger.LogInformation("SftpPollOrgJob: SFTP poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
