using Hangfire;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring "I am alive and my recurring-job pipeline is actually executing" beat
/// (every 2 min). Emits a single structured log line each run.
///
/// WHY this exists alongside Hangfire's built-in server heartbeat:
///   - Hangfire writes a server heartbeat to Postgres every ~30 s as long as the
///     <c>BackgroundJobServer</c> thread is alive. The readiness endpoint
///     (<c>WorkerHeartbeatHealthCheck</c>) and <c>OpsHealthService</c> read THAT to
///     answer "is a Worker registered + recently beating?".
///   - But a registered server whose <em>recurring-job dispatcher</em> has wedged
///     (deadlocked job, poisoned queue, storage write failure on the recurring
///     table) would STILL send server heartbeats while NO recurring job ever fires —
///     uploads silently never parse/transform/deliver. The Hangfire heartbeat alone
///     cannot distinguish "server thread alive" from "recurring jobs executing".
///   - This job closes that gap cheaply: it is itself a recurring job, so its log
///     line only appears if the recurring dispatcher is genuinely draining the
///     schedule. Ops greps the Railway Worker logs for "WORKER-HEARTBEAT" (and the
///     Sentry breadcrumb trail) to confirm end-to-end recurring-job liveness.
///
/// Deliberately does NOT write a heartbeat row to a new table: that signal is
/// already covered by Hangfire's server heartbeat (which the readiness check reads),
/// so a new table + migration would add storage churn without adding signal. The
/// observable artifact here is the log line / breadcrumb, not DB state.
///
/// Idempotent: it only logs (no side effects, no state mutation), so a Hangfire
/// retry or overlapping run is harmless.
/// </summary>
public sealed class WorkerHeartbeatJob
{
    private readonly ILogger<WorkerHeartbeatJob> _logger;

    public WorkerHeartbeatJob(ILogger<WorkerHeartbeatJob> logger) => _logger = logger;

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public Task ExecuteAsync(CancellationToken ct)
    {
        // Single structured line — Information level so it also drops a Sentry
        // breadcrumb (MinimumBreadcrumbLevel = Information on the Worker) without
        // raising a Sentry event. UtcNow is the explicit "alive @" timestamp.
        _logger.LogInformation(
            "WORKER-HEARTBEAT: recurring-job pipeline alive @ {UtcNow:o} (machine {Machine}).",
            DateTime.UtcNow,
            Environment.MachineName);

        return Task.CompletedTask;
    }
}
