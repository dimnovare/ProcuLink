using Hangfire;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IRecurringJobManager _recurringJobs;

    public Worker(ILogger<Worker> logger, IRecurringJobManager recurringJobs)
    {
        _logger = logger;
        _recurringJobs = recurringJobs;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcuLink Worker starting up...");

        _recurringJobs.AddOrUpdate<EmailPollingJob>(
            "email-polling",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        _recurringJobs.AddOrUpdate<SftpPollingJob>(
            "sftp-polling",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        _recurringJobs.AddOrUpdate<S3PollingJob>(
            "s3-polling",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        // P0 reliability: detect orders stuck in parsing/transforming (every 15 min).
        _recurringJobs.AddOrUpdate<StuckOrderDetectionJob>(
            "stuck-order-detection",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/15 * * * *");

        // Group O reliability: flag deliveries that miss their SLA confirmation window (every 15 min).
        _recurringJobs.AddOrUpdate<DeliverySlaSweepJob>(
            "delivery-sla-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/15 * * * *");

        // Delivery-path hardening: recover orders stranded in 'delivering' past the threshold
        // (every 15 min). Re-drives via RetryDeliveryJob up to a cap, then dead-letters —
        // guarantees a delivering→terminal transition. Companion to stuck-order-detection.
        _recurringJobs.AddOrUpdate<StuckDeliveryDetectionJob>(
            "stuck-delivery-detection",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/15 * * * *");

        // Reliability/observability: alert (Sentry) when no worker is beating or the dead-letter /
        // failed-delivery backlog spikes (every 5 min). No-op without a Sentry DSN; rate-limited.
        _recurringJobs.AddOrUpdate<WorkerHealthAlertJob>(
            "worker-health-alert",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        // Maintenance: prune append-only / ephemeral tables past their retention window so they do
        // not grow unbounded (daily at 03:30 UTC). Disabled by default — config-gated, bounded batches.
        _recurringJobs.AddOrUpdate<DataRetentionSweepJob>(
            "data-retention-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "30 3 * * *");

        _logger.LogInformation("Registered recurring jobs: email-polling, sftp-polling, s3-polling, worker-health-alert (every 5 min), stuck-order-detection + delivery-sla-sweep + stuck-delivery-detection (every 15 min), data-retention-sweep (daily 03:30 UTC).");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProcuLink Worker is running. Waiting for jobs...");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Placeholder: In future, this will poll for pending jobs to process
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcuLink Worker is stopping.");
        return base.StopAsync(cancellationToken);
    }
}
