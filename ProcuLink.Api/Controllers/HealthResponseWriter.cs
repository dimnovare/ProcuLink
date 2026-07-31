using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Serialises the readiness <see cref="HealthReport"/> to a structured JSON body
/// for <c>GET /health/ready</c>, replacing the default plain-text "Healthy" string.
///
/// The shape is stable + machine-readable so the external uptime workflow
/// (.github/workflows/uptime.yml) and any dashboard can parse it:
/// <code>
/// {
///   "status": "Healthy" | "Degraded" | "Unhealthy",
///   "ready": true,                       // false only when status == Unhealthy (HTTP 503)
///   "workerHealthy": true,               // flattened from the "worker" check — the uptime
///                                        // workflow fails on false even while HTTP is 200
///   "totalDurationMs": 12.3,
///   "checks": [
///     { "name": "database",   "status": "Healthy",  "description": "Database reachable.",        "durationMs": 4.1, "data": {} },
///     { "name": "storage",    "status": "Healthy",  "description": "Storage reachable.",         "durationMs": 1.0, "data": {} },
///     { "name": "migrations", "status": "Healthy",  "description": "Migrations applied …",       "durationMs": 0.0, "data": {} },
///     { "name": "worker",     "status": "Degraded", "description": "Worker heartbeat is stale …", "durationMs": 0.2,
///       "data": { "activeWorkers": 0, "secondsSinceWorkerHeartbeat": 312.4, "heartbeatDeadlineSeconds": 60 } }
///   ]
/// }
/// </code>
///
/// SECURITY: this writer NEVER serialises exception stack traces, connection
/// strings, credentials, or any secret. Each check returns only counts / ages /
/// booleans in its <c>Data</c> bag and a human-readable <c>Description</c>; the
/// underlying <see cref="HealthReportEntry.Exception"/> is deliberately NOT
/// included. So a public, unauthenticated probe is safe to expose.
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // camelCase + null omission keeps the body compact and JS-friendly.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task WriteReadinessJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        // Flatten the "worker" check's healthy/degraded status into a single boolean
        // so the uptime workflow can `jq -e '.workerHealthy'` without walking checks.
        // A Degraded worker keeps HTTP 200, so this flag is the only signal of a dead
        // Worker on the happy-path response.
        var workerHealthy = !report.Entries.TryGetValue("worker", out var worker)
            || worker.Status == HealthStatus.Healthy;

        // WP-21: flatten the revision-authority check's EFFECTIVE value the same way, so the one
        // configuration fact that decides whether ProcuLink's reproducibility claims are true can
        // be read with `jq -e '.revisionAuthority'` instead of a Railway shell. Consumed by
        // .github/workflows/uptime.yml, which FAILS the run when it is not true — the flag going
        // off is otherwise completely silent (orders keep flowing, they just stop honouring pins).
        //
        // Renders false when the check is absent, matching the code default — a missing check must
        // never read as "on". That deliberately conflates "flag off" with "check missing" in this
        // one boolean; the two are told apart by looking for a `revisionAuthority` entry in
        // checks[], and docs/ops/revision-authority-production-smoke.md §2 walks an operator
        // through exactly that. Conflating them is the safe direction: both mean "not confirmed
        // on", which is what the uptime workflow should fail on.
        var revisionAuthority =
            report.Entries.TryGetValue("revisionAuthority", out var revAuth)
            && revAuth.Data.TryGetValue("enabled", out var enabled)
            && enabled is true;

        var payload = new
        {
            status = report.Status.ToString(),
            // `ready` tracks the HTTP contract: Unhealthy → 503 (not ready), else 200.
            ready = report.Status != HealthStatus.Unhealthy,
            workerHealthy,
            revisionAuthority,
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries
                .Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    // Data is the check-authored bag of counts/ages — never secrets.
                    data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                })
                .OrderBy(c => c.name, StringComparer.Ordinal)
                .ToArray(),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
