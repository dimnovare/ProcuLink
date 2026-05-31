using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring dispatcher job (every 5 min): enumerates all organisations that have an
/// email config and enqueues one <see cref="EmailPollOrgJob"/> child job per org.
/// Each child runs independently so a slow or hung IMAP server cannot block other orgs.
/// </summary>
public sealed class EmailPollingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<EmailPollingJob> _logger;

    public EmailPollingJob(
        ProcuLinkDbContext db,
        IBackgroundJobClient jobs,
        ILogger<EmailPollingJob> logger)
    {
        _db = db;
        _jobs = jobs;
        _logger = logger;
    }

    [Queue("polling")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Email polling dispatcher run started.");

        var orgIds = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.EmailConfigJson != "{}")
            .Select(x => x.Id)
            .ToListAsync(ct);

        _logger.LogInformation("Email polling dispatcher: found {Count} org(s) with email config. Enqueuing child jobs.", orgIds.Count);

        foreach (var orgId in orgIds)
        {
            _jobs.Enqueue<EmailPollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None));
        }

        _logger.LogInformation("Email polling dispatcher: enqueued {Count} EmailPollOrgJob(s).", orgIds.Count);
    }
}
