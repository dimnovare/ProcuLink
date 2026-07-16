using Hangfire;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Jobs;

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
    // PER-ORG lock: PerOrgDistributedMutex acquires a storage-backed distributed lock keyed on the
    // orgId ARGUMENT (resource "poll:SftpPollOrgJob.ExecuteAsync:{orgId}"), so two SFTP polls for
    // the SAME org can never overlap, while two DIFFERENT orgs never contend and poll in parallel.
    // NOT [DisableConcurrentExecution]: on OSS Hangfire that keys on job TYPE + METHOD ONLY (never
    // the arguments), which serialised EVERY org's SFTP polling through one lock — one slow or hung
    // tenant endpoint stalled all other tenants for up to the timeout.
    // Per-org granularity is sufficient because the lock is only the OUTER guard: the claim-first
    // ledger insert + unique index in SftpIngressService is what actually guarantees exactly-one
    // order, and two different orgs share no state to race over.
    [PerOrgDistributedMutex(orgArgumentIndex: 0, timeoutSeconds: 300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("SftpPollOrgJob: starting SFTP poll for org {OrgId}.", orgId);

        var count = await _sftpIngress.PollAsync(orgId, ct);

        _logger.LogInformation("SftpPollOrgJob: SFTP poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
