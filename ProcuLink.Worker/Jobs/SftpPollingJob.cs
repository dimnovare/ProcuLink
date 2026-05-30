using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring dispatcher job (every 5 min): enumerates all SFTP-enabled organisations
/// and enqueues one <see cref="SftpPollOrgJob"/> child job per org.
/// Each child runs independently so a slow or hung SFTP server cannot block other orgs.
/// </summary>
public sealed class SftpPollingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SftpPollingJob> _logger;

    public SftpPollingJob(
        ProcuLinkDbContext db,
        IBackgroundJobClient jobs,
        ILogger<SftpPollingJob> logger)
    {
        _db = db;
        _jobs = jobs;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SFTP polling dispatcher run started.");

        var enabledOrgIds = await _db.Set<SftpIngressConfig>()
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.OrgId)
            .Distinct()
            .ToListAsync(ct);

        _logger.LogInformation("SFTP polling dispatcher: found {Count} org(s) with enabled SFTP config. Enqueuing child jobs.", enabledOrgIds.Count);

        foreach (var orgId in enabledOrgIds)
        {
            _jobs.Enqueue<SftpPollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None));
        }

        _logger.LogInformation("SFTP polling dispatcher: enqueued {Count} SftpPollOrgJob(s).", enabledOrgIds.Count);
    }
}
