using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Hangfire recurring job that polls all S3/R2-enabled organisations for new
/// purchase-order files, mirroring the cadence of <see cref="SftpPollingJob"/>
/// and <see cref="EmailPollingJob"/>.
/// </summary>
public sealed class S3PollingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IS3IngressService _s3Ingress;
    private readonly ILogger<S3PollingJob> _logger;

    public S3PollingJob(
        ProcuLinkDbContext db,
        IS3IngressService s3Ingress,
        ILogger<S3PollingJob> logger)
    {
        _db = db;
        _s3Ingress = s3Ingress;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("S3 polling run started.");

        var enabledOrgIds = await _db.Set<S3IngressConfig>()
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.OrgId)
            .Distinct()
            .ToListAsync(ct);

        _logger.LogInformation("S3 polling: found {Count} org(s) with enabled S3 config.", enabledOrgIds.Count);

        var polled = 0;
        var errored = 0;
        var totalFiles = 0;

        foreach (var orgId in enabledOrgIds)
        {
            try
            {
                var count = await _s3Ingress.PollAsync(orgId, ct);
                totalFiles += count;
                polled++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "S3 polling failed for org {OrgId}.", orgId);
                errored++;
            }
        }

        _logger.LogInformation(
            "S3 polling run complete. Polled={Polled}, Errored={Errored}, FilesImported={Files}.",
            polled, errored, totalFiles);
    }
}
