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
    // Per-org lock: DisableConcurrentExecution keys on the method + args, and this child takes
    // orgId as its argument, so two SFTP polls for the SAME org can never overlap. With the
    // claim-first ledger insert in SftpIngressService this closes the concurrent-duplicate window.
    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(Guid orgId, CancellationToken ct)
    {
        _logger.LogInformation("SftpPollOrgJob: starting SFTP poll for org {OrgId}.", orgId);

        var count = await _sftpIngress.PollAsync(orgId, ct);

        _logger.LogInformation("SftpPollOrgJob: SFTP poll complete for org {OrgId}. FilesImported={Files}.", orgId, count);
    }
}
