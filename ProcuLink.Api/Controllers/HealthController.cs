using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Liveness probe — fast, dependency-free 200.
    ///
    /// Railway's container health probe hits this path. It must NEVER depend on a
    /// slow/flaky dependency (DB cold-start, R2 round-trip): if it did, a transient
    /// dependency blip would make Railway kill a process that is otherwise serving
    /// fine. Dependency status lives on the readiness endpoint instead — see
    /// <c>MapHealthChecks("/health/ready")</c> in Program.cs (tag "ready").
    /// </summary>
    [HttpGet("/health")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok("OK");
    }
}

/// <summary>
/// Process-wide tri-state readiness indicator for the background auto-migration.
///
/// The fire-and-forget migration in Program.cs (ApplicationStarted) keeps the
/// HTTP server up so the liveness probe passes during Neon cold-start. The
/// tri-state lets the readiness endpoint be honest during the migrate window:
/// <list type="bullet">
///   <item><term>Pending</term><description>Default — migration not yet completed.
///     Reports NOT-ready so early traffic does not hit a stale schema.</description></item>
///   <item><term>Succeeded</term><description>MigrateAsync completed OK.
///     Reports Healthy.</description></item>
///   <item><term>Failed</term><description>All retries exhausted.
///     Reports NOT-ready (schema may be stale).</description></item>
/// </list>
/// </summary>
public enum MigrationReadinessState
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
}

public static class MigrationReadiness
{
    // _state is written from a single background thread after startup and read
    // from the health-check path. Volatile int (backing the enum) gives the
    // visibility guarantee without a lock.
    private static volatile int _state = (int)MigrationReadinessState.Pending;

    /// <summary>Current tri-state readiness.</summary>
    public static MigrationReadinessState State =>
        (MigrationReadinessState)_state;

    /// <summary>True once the background migration has exhausted all retries.</summary>
    public static bool HasFailed =>
        (MigrationReadinessState)_state == MigrationReadinessState.Failed;

    /// <summary>Called by the migration loop after the final failed attempt.</summary>
    public static void MarkFailed() =>
        _state = (int)MigrationReadinessState.Failed;

    /// <summary>
    /// Called when migrations apply successfully. Clears any prior failure or
    /// pending state so the readiness endpoint reports healthy.
    /// </summary>
    public static void MarkSucceeded() =>
        _state = (int)MigrationReadinessState.Succeeded;

    /// <summary>
    /// Resets to Pending — used in tests to simulate a freshly starting process,
    /// and called at the start of the migration task in Program.cs so the state
    /// is honest from the very beginning of the startup window.
    /// </summary>
    public static void MarkPending() =>
        _state = (int)MigrationReadinessState.Pending;

    /// <summary>
    /// Alias for <see cref="MarkPending"/> kept for test symmetry.
    /// </summary>
    public static void Reset() =>
        _state = (int)MigrationReadinessState.Pending;
}

/// <summary>
/// Readiness check that the database is reachable. Uses EF Core's
/// <c>Database.CanConnectAsync</c> (no extra HealthChecks.EntityFrameworkCore
/// package needed) — it opens a connection and runs the provider's trivial probe
/// query, so a down/unreachable DB reports Unhealthy on the readiness endpoint.
/// Tagged "ready" — never affects liveness.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ProcuLinkDbContext _db;

    public DatabaseHealthCheck(ProcuLinkDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable (CanConnect=false).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity probe threw.", ex);
        }
    }
}

/// <summary>
/// Readiness check that reflects the tri-state migration readiness.
/// <list type="bullet">
///   <item><term>Pending</term><description>Migration has not yet completed —
///     reports Unhealthy so early health-check traffic does not see a false-ready
///     while the schema might be stale.</description></item>
///   <item><term>Failed</term><description>All retries exhausted — reports
///     Unhealthy; schema is likely stale.</description></item>
///   <item><term>Succeeded</term><description>Reports Healthy.</description></item>
/// </list>
/// Tagged "ready" so it only affects the readiness endpoint, never liveness.
/// </summary>
public sealed class MigrationReadinessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = MigrationReadiness.State switch
        {
            MigrationReadinessState.Succeeded =>
                HealthCheckResult.Healthy("Migrations applied successfully."),
            MigrationReadinessState.Failed =>
                HealthCheckResult.Unhealthy(
                    "Database migrations failed after all retry attempts — schema may be stale."),
            _ /* Pending */ =>
                HealthCheckResult.Unhealthy(
                    "Database migrations have not yet completed — service is still starting up."),
        };
        return Task.FromResult(result);
    }
}

/// <summary>
/// Lightweight storage (Cloudflare R2 / local) reachability check for the
/// readiness endpoint. Tagged "ready".
///
/// It exercises the real <see cref="IFileStorageService"/> via the cheapest
/// available operation (<see cref="IFileStorageService.GetSignedDownloadUrlAsync"/>):
/// for R2 this is a local signing round-trip that fails fast if the client/bucket
/// config is broken; for the local dev provider it is a no-op path build. We do
/// NOT perform a real network PUT/HEAD on the hot readiness path — that would add
/// latency and could flap. If storage isn't configured (local dev / R2 keys
/// absent) the check degrades to <c>Healthy</c> with a note rather than failing,
/// so a dev environment without R2 still reports ready.
/// </summary>
public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly IFileStorageService _storage;

    public StorageHealthCheck(IFileStorageService storage) => _storage = storage;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Cheap, side-effect-free probe: minting a signed URL constructs the
            // S3/R2 request signer (R2) or builds a local path (dev). A misconfigured
            // bucket/endpoint/credentials throw here synchronously.
            var url = await _storage.GetSignedDownloadUrlAsync(
                key: "__healthcheck__/probe",
                expiry: TimeSpan.FromMinutes(1),
                ct: cancellationToken);

            return string.IsNullOrWhiteSpace(url)
                ? HealthCheckResult.Degraded("Storage returned an empty signed URL.")
                : HealthCheckResult.Healthy("Storage reachable.");
        }
        catch (Exception ex)
        {
            // Degraded (not Unhealthy): storage signing trouble shouldn't, on its
            // own, take the whole process out of rotation the way a dead DB should.
            return HealthCheckResult.Degraded(
                "Storage reachability probe failed.", ex);
        }
    }
}

/// <summary>
/// Readiness check that a background Worker is alive and recently beating.
///
/// The Worker is a SEPARATE Railway process ("aware-amazement") that runs the
/// Hangfire server; the API only enqueues. The classic prod incident class is
/// "Worker not consuming" — the API keeps 200ing while uploads silently never
/// parse/transform/deliver because nothing drains the queue. This check makes
/// that visible on /health/ready.
///
/// MECHANISM — NO new table / no new heartbeat job. Hangfire already writes a
/// per-server heartbeat into the SHARED Postgres storage every ~30 s. The API
/// shares that storage and has <see cref="IMonitoringApi"/> registered
/// (Program.cs), so we read the most-recent server heartbeat directly. A
/// heartbeat older than <see cref="HeartbeatDeadlineSeconds"/> (or no server at
/// all) means the Worker is down/stale. This mirrors the freshness logic in
/// <c>OpsHealthService.GetWorkerHealth()</c> (60 s deadline) so the two agree.
///
/// SEVERITY = Degraded, NOT Unhealthy. A dead Worker must NOT take the API out
/// of rotation — the API still serves reads/billing/the dashboard, and flapping
/// the API's own readiness on a Worker blip would be worse than the Worker
/// outage. Degraded keeps /health/ready at HTTP 200; the structured JSON body
/// carries <c>workerHealthy:false</c>, and the external uptime workflow (and the
/// recurring <c>WorkerHealthAlertJob</c> → Sentry) alert on that flag. So the
/// founder still gets paged, without the API self-evicting.
///
/// Tagged "ready" — never affects the liveness probe.
/// </summary>
public sealed class WorkerHeartbeatHealthCheck : IHealthCheck
{
    /// <summary>
    /// A Hangfire server heartbeat older than this (seconds) marks the Worker
    /// stale. Hangfire's default HeartbeatInterval is 30 s; 60 s allows one
    /// missed beat of leeway. Kept in lock-step with
    /// <c>OpsHealthService.WorkerHeartbeatDeadline</c>.
    /// </summary>
    public const int HeartbeatDeadlineSeconds = 60;

    // Nullable: in test hosts (and before Hangfire storage is ready) no
    // monitoring API is available. A null/throwing probe degrades gracefully.
    private readonly IMonitoringApi? _monitoring;

    public WorkerHeartbeatHealthCheck(IMonitoringApi? monitoring = null) =>
        _monitoring = monitoring;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = Probe(_monitoring);

        // Surface the same machine-readable fields on the JSON body via the check's
        // data bag (no secrets — just counts + ages).
        var data = new Dictionary<string, object>
        {
            ["activeWorkers"] = probe.ActiveWorkers,
            ["heartbeatDeadlineSeconds"] = HeartbeatDeadlineSeconds,
        };
        if (probe.SecondsSinceHeartbeat is { } secs)
            data["secondsSinceWorkerHeartbeat"] = Math.Round(secs, 1);

        if (probe.Healthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"Worker beating ({probe.ActiveWorkers} registered, " +
                $"{probe.SecondsSinceHeartbeat:F0}s since last heartbeat).",
                data));
        }

        var reason = probe.ActiveWorkers == 0
            ? "No Hangfire worker is registered — background jobs (parse/transform/deliver) are NOT being processed."
            : $"Worker heartbeat is stale ({probe.SecondsSinceHeartbeat:F0}s > {HeartbeatDeadlineSeconds}s deadline) — the Worker may be hung or restarting.";

        // Degraded (not Unhealthy): keep /health/ready at 200; the JSON flag +
        // alert job carry the page. See the class remarks for the rationale.
        return Task.FromResult(HealthCheckResult.Degraded(reason, data: data));
    }

    /// <summary>
    /// Reads the most-recent Hangfire server heartbeat. Returns safe defaults
    /// (0 workers, unhealthy) when the monitoring API is null/unavailable or
    /// throws — the readiness endpoint must never fail because the Hangfire
    /// storage is momentarily unreachable.
    /// </summary>
    internal static WorkerHeartbeatProbe Probe(IMonitoringApi? monitoring)
    {
        try
        {
            var api = monitoring ?? Hangfire.JobStorage.Current?.GetMonitoringApi();
            if (api is null)
                return new WorkerHeartbeatProbe(0, null, false);

            var servers = api.Servers();
            if (servers is null || servers.Count == 0)
                return new WorkerHeartbeatProbe(0, null, false);

            var lastHeartbeat = servers
                .Select(s => s.Heartbeat)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .DefaultIfEmpty()
                .Max();

            if (lastHeartbeat == default)
                return new WorkerHeartbeatProbe(servers.Count, null, false);

            var age = (DateTime.UtcNow - lastHeartbeat).TotalSeconds;
            var healthy = age <= HeartbeatDeadlineSeconds;
            return new WorkerHeartbeatProbe(servers.Count, age, healthy);
        }
        catch
        {
            // Storage unavailable / test env — degrade, never throw.
            return new WorkerHeartbeatProbe(0, null, false);
        }
    }

    /// <summary>Result of a single Hangfire-heartbeat probe.</summary>
    internal readonly record struct WorkerHeartbeatProbe(
        int ActiveWorkers,
        double? SecondsSinceHeartbeat,
        bool Healthy);
}
