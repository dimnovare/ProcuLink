using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ProcuLink.Api.Controllers;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The readiness body's flattened booleans, tested at the writer rather than through the endpoint —
/// because the case that mattered CANNOT be produced through the endpoint.
///
/// <para><b>The defect.</b> <c>workerHealthy</c> was computed as
/// <c>!Entries.TryGetValue("worker", out var w) || w.Status == Healthy</c>, so an ABSENT "worker"
/// entry rendered TRUE. The entry goes missing exactly when the check was dropped from the
/// registration, threw during construction, or fell out of the "ready" tag filter — every one of
/// which is a state where nobody knows whether the Worker is consuming. <c>uptime.yml</c> fails the
/// run on <c>workerHealthy != true</c>, so the open polarity turned "the Worker monitor
/// disappeared" into a green probe. Its neighbour eight lines below, <c>revisionAuthority</c>, was
/// deliberately built absent→false with the rationale written down.</para>
///
/// <para><b>Why not an endpoint test.</b> <c>HealthEndpointTests</c> covers present+Healthy and
/// present+Degraded, which is both directions of the STATUS but neither direction of the presence
/// question — and it cannot cover the absent case at all, because Program.cs always registers the
/// check. Reaching the defect requires handing the writer a report that lacks the entry, which is
/// what these tests do.</para>
/// </summary>
public class HealthResponseWriterFlagPolarityTests
{
    // ── workerHealthy ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkerHealthy_IsFalse_WhenTheWorkerCheckIsAbsent()
    {
        var body = await WriteAsync(Report());   // no "worker" entry at all

        Assert.False(body.GetProperty("workerHealthy").GetBoolean());
    }

    [Fact]
    public async Task WorkerHealthy_IsTrue_OnlyWhenTheCheckRanAndPassed()
    {
        var body = await WriteAsync(Report(("worker", HealthStatus.Healthy)));

        Assert.True(body.GetProperty("workerHealthy").GetBoolean());
    }

    [Fact]
    public async Task WorkerHealthy_IsFalse_WhenTheCheckRanAndDegraded()
    {
        var body = await WriteAsync(Report(("worker", HealthStatus.Degraded)));

        Assert.False(body.GetProperty("workerHealthy").GetBoolean());
    }

    // ── recurringJobsHealthy ─────────────────────────────────────────────────────

    [Fact]
    public async Task RecurringJobsHealthy_IsFalse_WhenTheCheckIsAbsent()
    {
        var body = await WriteAsync(Report(("worker", HealthStatus.Healthy)));

        Assert.True(
            body.TryGetProperty("recurringJobsHealthy", out var flag),
            "the readiness body must carry recurringJobsHealthy — a wedged recurring-job dispatcher "
            + "keeps the Hangfire server heartbeat fresh, so workerHealthy cannot report it");
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    [Fact]
    public async Task RecurringJobsHealthy_IsTrue_OnlyWhenTheCheckRanAndPassed()
    {
        var body = await WriteAsync(Report(("recurringJobs", HealthStatus.Healthy)));

        Assert.True(body.GetProperty("recurringJobsHealthy").GetBoolean());
    }

    [Fact]
    public async Task RecurringJobsHealthy_IsFalse_WhenTheCheckRanAndDegraded()
    {
        var body = await WriteAsync(Report(("recurringJobs", HealthStatus.Degraded)));

        Assert.False(body.GetProperty("recurringJobsHealthy").GetBoolean());
    }

    /// <summary>
    /// The rule, stated once for all three flags: an absent entry is never evidence of health.
    /// A future flag added with the old polarity fails here.
    /// </summary>
    [Fact]
    public async Task EveryFlattenedFlag_IsFalse_OnAReportWithNoChecksAtAll()
    {
        var body = await WriteAsync(Report());

        foreach (var flag in new[] { "workerHealthy", "recurringJobsHealthy", "revisionAuthority" })
        {
            Assert.True(body.TryGetProperty(flag, out var value), $"`{flag}` must always be present");
            Assert.Equal(JsonValueKind.False, value.ValueKind);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static HealthReport Report(params (string Name, HealthStatus Status)[] entries)
    {
        var map = entries.ToDictionary(
            e => e.Name,
            e => new HealthReportEntry(
                e.Status,
                description: $"{e.Name} check",
                duration: TimeSpan.FromMilliseconds(1),
                exception: null,
                data: new Dictionary<string, object>()));

        return new HealthReport(map, TimeSpan.FromMilliseconds(5));
    }

    private static async Task<JsonElement> WriteAsync(HealthReport report)
    {
        var context = new DefaultHttpContext();
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await HealthResponseWriter.WriteReadinessJsonAsync(context, report);

        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray())).RootElement.Clone();
    }
}
