using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Hangfire recurring job that polls all SFTP-enabled organisations for new
/// purchase-order files every 5 minutes, mirroring the cadence of
/// <see cref="EmailPollingJob"/>.
/// </summary>
public sealed class SftpPollingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly ISftpIngressService _sftpIngress;
    private readonly ILogger<SftpPollingJob> _logger;

    public SftpPollingJob(
        ProcuLinkDbContext db,
        ISftpIngressService sftpIngress,
        ILogger<SftpPollingJob> logger)
    {
        _db = db;
        _sftpIngress = sftpIngress;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SFTP polling run started.");

        var enabledOrgIds = await _db.Set<SftpIngressConfig>()
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.OrgId)
            .Distinct()
            .ToListAsync(ct);

        _logger.LogInformation("SFTP polling: found {Count} org(s) with enabled SFTP config.", enabledOrgIds.Count);

        var polled = 0;
        var skipped = 0;
        var totalFiles = 0;

        foreach (var orgId in enabledOrgIds)
        {
            try
            {
                var count = await _sftpIngress.PollAsync(orgId, ct);
                totalFiles += count;
                polled++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SFTP polling failed for org {OrgId}.", orgId);
                skipped++;
            }
        }

        _logger.LogInformation(
            "SFTP polling run complete. Polled={Polled}, Errored={Errored}, FilesImported={Files}.",
            polled, skipped, totalFiles);
    }
}
