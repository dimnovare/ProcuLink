using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring dispatcher job (every 5 min): enumerates all S3/R2-enabled organisations
/// and enqueues one <see cref="S3PollOrgJob"/> child job per org.
/// Each child runs independently so a slow or hung S3 endpoint cannot block other orgs.
/// </summary>
public sealed class S3PollingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<S3PollingJob> _logger;

    public S3PollingJob(
        ProcuLinkDbContext db,
        IBackgroundJobClient jobs,
        ILogger<S3PollingJob> logger)
    {
        _db = db;
        _jobs = jobs;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("S3 polling dispatcher run started.");

        var enabledOrgIds = await _db.Set<S3IngressConfig>()
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.OrgId)
            .Distinct()
            .ToListAsync(ct);

        _logger.LogInformation("S3 polling dispatcher: found {Count} org(s) with enabled S3 config. Enqueuing child jobs.", enabledOrgIds.Count);

        foreach (var orgId in enabledOrgIds)
        {
            _jobs.Enqueue<S3PollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None));
        }

        _logger.LogInformation("S3 polling dispatcher: enqueued {Count} S3PollOrgJob(s).", enabledOrgIds.Count);
    }
}
