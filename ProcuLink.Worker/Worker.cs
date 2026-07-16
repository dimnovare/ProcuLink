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

        // Supplier-catalog pull sync (SFTP/FTP/FTPS): hourly dispatcher fanning out one
        // child job per DUE source (per-supplier isolation; soft lock prevents overlap).
        _recurringJobs.AddOrUpdate<ProcuLink.Infrastructure.Jobs.CatalogSyncDispatcherJob>(
            "catalog-sync",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 * * * *");

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

        // B1 lost-order backstop: recover orders stranded in 'ready_to_deliver' whose delivery enqueue
        // was lost between TransformOrderJob's commit and DeliverOrderJob.Enqueue (every 15 min,
        // 30-min aged threshold). Re-drives AUTO-deliver orders with no delivery attempt through the
        // normal first-delivery job; idempotent (DeliverOrderJob's claim prevents double-send).
        _recurringJobs.AddOrUpdate<StrandedReadyDeliveryDetectionJob>(
            "stranded-ready-delivery-detection",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/15 * * * *");

        // B5 silent-no-more-retries backstop: recover orders stranded in 'delivery_failed' whose
        // automatic next-retry was lost between the failed-attempt write and the backoff schedule
        // (crash / lost enqueue). Hourly with a 3-hour aged threshold — deliberately well past the
        // max retry backoff (≤2h) so a legitimately-scheduled retry is never raced; only a genuinely
        // lost reschedule ages this long. Re-drives via RetryDeliveryJob (idempotent).
        _recurringJobs.AddOrUpdate<StrandedFailedDeliveryDetectionJob>(
            "stranded-failed-delivery-detection",
            job => job.ExecuteAsync(CancellationToken.None),
            "10 * * * *");

        // Reliability/observability: alert (Sentry) when no worker is beating or the dead-letter /
        // failed-delivery backlog spikes (every 5 min). No-op without a Sentry DSN; rate-limited.
        _recurringJobs.AddOrUpdate<WorkerHealthAlertJob>(
            "worker-health-alert",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *");

        // Liveness beat: a log line + Sentry breadcrumb every 2 min proving the recurring-job
        // dispatcher is actually firing (not just the Hangfire server thread being alive).
        // Grep the Worker logs for "WORKER-HEARTBEAT" to confirm end-to-end liveness.
        _recurringJobs.AddOrUpdate<WorkerHeartbeatJob>(
            "worker-heartbeat",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/2 * * * *");

        // Maintenance: prune append-only / ephemeral tables past their retention window so they do
        // not grow unbounded (daily at 03:30 UTC). Disabled by default — config-gated, bounded batches.
        _recurringJobs.AddOrUpdate<DataRetentionSweepJob>(
            "data-retention-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "30 3 * * *");

        // Blob retention: purge R2 file blobs of TERMINAL orders past each opted-in org's
        // retention_days window (daily at 04:15 UTC, after the row sweep). DOUBLE-latched
        // OFF by default: per-org retention_days NULL + global Retention:DryRun=true.
        // Blob-only — DB rows, hashes, provenance and audit trail always stay.
        _recurringJobs.AddOrUpdate<BlobRetentionSweepJob>(
            "blob-retention-sweep",
            job => job.ExecuteAsync(CancellationToken.None),
            "15 4 * * *");

        // Billing safety net: reconcile every org's plan/account_status against Stripe as source
        // of truth (daily 02:00 UTC). Backstops missed customer.subscription.* webhooks and stale/
        // test-mode subscription ids; downgrades a vanished subscription to frozen Pilot + read_only
        // only after a 3-day confirmed-missing grace. Idempotent; no-op without a configured Stripe key.
        _recurringJobs.AddOrUpdate<BillingReconciliationJob>(
            "billing-reconciliation",
            job => job.ExecuteAsync(CancellationToken.None),
            "0 2 * * *");

        _logger.LogInformation("Registered recurring jobs: worker-heartbeat (every 2 min), email-polling, sftp-polling, s3-polling, worker-health-alert (every 5 min), stuck-order-detection + delivery-sla-sweep + stuck-delivery-detection + stranded-ready-delivery-detection (every 15 min), catalog-sync + stranded-failed-delivery-detection (hourly), billing-reconciliation (daily 02:00 UTC), data-retention-sweep (daily 03:30 UTC), blob-retention-sweep (daily 04:15 UTC).");
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
