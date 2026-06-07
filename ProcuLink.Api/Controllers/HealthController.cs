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
/// Process-wide readiness flag for the background auto-migration.
///
/// The fire-and-forget migration in Program.cs (ApplicationStarted) keeps the
/// HTTP server up so the liveness probe passes during Neon cold-start. But if it
/// exhausts all retries the schema may be stale and the process should report
/// NOT ready. This flag is the bridge: the migration loop flips it on final
/// failure, and <see cref="MigrationReadinessHealthCheck"/> surfaces it on the
/// readiness endpoint so Railway / external monitoring can see the degraded state
/// (the process stays alive — only readiness flips).
/// </summary>
public static class MigrationReadiness
{
    private static volatile bool _failed;

    /// <summary>True once the background migration has exhausted all retries.</summary>
    public static bool HasFailed => _failed;

    /// <summary>Called by the migration loop after the final failed attempt.</summary>
    public static void MarkFailed() => _failed = true;

    /// <summary>
    /// Called when migrations apply successfully. Clears any prior failure so a
    /// later successful re-run (or test reuse of the static) reports healthy again.
    /// </summary>
    public static void MarkSucceeded() => _failed = false;
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
/// Readiness check that reports unhealthy when the background auto-migration has
/// exhausted all retry attempts (schema potentially stale). Tagged "ready" so it
/// only affects the readiness endpoint, never liveness.
/// </summary>
public sealed class MigrationReadinessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MigrationReadiness.HasFailed
            ? HealthCheckResult.Unhealthy(
                "Database migrations failed after all retry attempts — schema may be stale.")
            : HealthCheckResult.Healthy("Migrations applied (or in progress)."));
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
