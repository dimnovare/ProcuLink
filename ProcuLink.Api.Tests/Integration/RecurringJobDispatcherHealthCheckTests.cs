using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services.Alerting;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// A wedged Hangfire recurring-job DISPATCHER was invisible to every automated monitor.
///
/// <para><b>The gap.</b> Hangfire's server heartbeat is written by the <c>BackgroundJobServer</c>
/// thread every ~30 s for as long as that thread is alive. A server whose recurring-job dispatcher
/// has wedged — deadlocked job, poisoned queue, storage write failure on the recurring table — or
/// whose worker pool is saturated keeps writing it while NO scheduled job ever fires. So
/// <c>workerHealthy</c> stays true, <c>/health/ready</c> stays green, and uploads land and sit.
/// <c>WorkerHeartbeatJob</c> was written to close exactly this and closed it only for a human: its
/// own remarks say "Ops greps the Railway Worker logs for WORKER-HEARTBEAT".</para>
///
/// <para>These tests pin the automated replacement: the same evidence, read out of Hangfire's
/// recurring-job record instead of a log stream, on the readiness JSON.</para>
/// </summary>
public class RecurringJobDispatcherHealthCheckTests
{
    [Fact]
    public async Task ReportsHealthy_WhenEveryWatchedJobRanRecently()
    {
        var check = new RecurringJobDispatcherHealthCheck(FreshExecutions(minutesAgo: 1));

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data["healthy"]);
    }

    [Fact]
    public async Task ReportsDegraded_WhenAWatchedJobHasNotRunSinceItsDeadline()
    {
        // The wedge: the server is still beating (this check never looks at that), but the
        // dispatcher has not fired anything in 47 minutes.
        var check = new RecurringJobDispatcherHealthCheck(FreshExecutions(minutesAgo: 47));

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.False((bool)result.Data["healthy"]);
        Assert.Contains("worker-heartbeat", result.Description);
        Assert.Contains("47 min ago", result.Description);
    }

    /// <summary>
    /// Unknown is not healthy. The source returns <c>null</c> for "cannot tell" — an unreadable
    /// scheduler, or a job that has never executed — and treating that as proof of a working
    /// dispatcher is the same fail-open polarity the <c>workerHealthy</c> flag was just corrected
    /// for.
    /// </summary>
    [Fact]
    public async Task ReportsDegraded_WhenTheLastExecutionCannotBeRead()
    {
        var check = new RecurringJobDispatcherHealthCheck(new UnknownExecutions());

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("no readable last execution", result.Description);
    }

    /// <summary>
    /// The bag is the machine-readable half, so it is asserted as it reaches the wire — through the
    /// same writer <c>/health/ready</c> uses. An UNKNOWN age must be an absent field, never a zero:
    /// "0 minutes since last execution" would read as the freshest possible dispatcher.
    /// </summary>
    [Fact]
    public async Task DataBag_CarriesTheAgeOfEachWatchedJob_AndOmitsAnUnknownAge()
    {
        var known = await SerialiseCheckAsync(FreshExecutions(minutesAgo: 3));
        var unknown = await SerialiseCheckAsync(new UnknownExecutions());

        var knownJobs = known.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(
            RecurringJobDispatcherHealthCheck.Watched.Select(w => w.Id).ToArray(),
            knownJobs.Select(j => j.GetProperty("id").GetString()).ToArray());
        Assert.All(knownJobs, j =>
        {
            Assert.True(j.TryGetProperty("minutesSinceLastExecution", out var age));
            Assert.Equal(3.0, age.GetDouble());
            Assert.True(j.GetProperty("deadlineMinutes").GetInt32() > 0);
        });

        Assert.All(unknown.GetProperty("jobs").EnumerateArray(), j =>
            Assert.False(
                j.TryGetProperty("minutesSinceLastExecution", out _),
                "an unknown last execution must be an ABSENT field, not an age of zero"));

        // Counts, ages and ids of this repo's own jobs — no tenant data — so the bag is safe on the
        // anonymous probe, same rule as every other check.
        Assert.Equal(
            new[] { "healthy", "jobs" },
            known.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The watched ids are Hangfire strings, so a rename in <c>Worker.StartAsync</c> would make this
    /// check report a permanent, false stall. Rather than trust two literals to agree, this drives
    /// the REAL <c>Worker.StartAsync</c> and reads back what it registered.
    /// </summary>
    [Fact]
    public async Task EveryWatchedJobId_IsActuallyRegisteredByTheWorker()
    {
        var registrar = new RecordingRecurringJobManager();
        var worker = new ProcuLink.Worker.Worker(
            NullLogger<ProcuLink.Worker.Worker>.Instance, registrar);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotEmpty(registrar.Ids);   // anti-vacuity: StartAsync really registered something

        foreach (var watched in RecurringJobDispatcherHealthCheck.Watched)
        {
            Assert.True(
                registrar.Ids.Contains(watched.Id),
                $"the readiness check watches recurring job '{watched.Id}', which "
                + "ProcuLink.Worker.Worker.StartAsync does not register — the check would report a "
                + "permanent, false stall. Registered ids: " + string.Join(", ", registrar.Ids));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static HealthCheckContext Context() => new();

    /// <summary>
    /// Runs the check and returns the <c>recurringJobs</c> entry's <c>data</c> object exactly as
    /// <c>HealthResponseWriter</c> renders it onto <c>/health/ready</c>.
    /// </summary>
    private static async Task<JsonElement> SerialiseCheckAsync(IRecurringJobLastExecutionSource source)
    {
        var result = await new RecurringJobDispatcherHealthCheck(source).CheckHealthAsync(Context());

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["recurringJobs"] = new(
                    result.Status, result.Description, TimeSpan.FromMilliseconds(1),
                    exception: null, data: result.Data),
            },
            TimeSpan.FromMilliseconds(2));

        var context = new DefaultHttpContext();
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        await HealthResponseWriter.WriteReadinessJsonAsync(context, report);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        return doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "recurringJobs")
            .GetProperty("data")
            .Clone();
    }

    private sealed class FixedAgeExecutions : IRecurringJobLastExecutionSource
    {
        private readonly int _minutesAgo;
        public FixedAgeExecutions(int minutesAgo) => _minutesAgo = minutesAgo;
        public DateTime? GetLastExecutionUtc(string recurringJobId) =>
            DateTime.UtcNow.AddMinutes(-_minutesAgo);
    }

    private static IRecurringJobLastExecutionSource FreshExecutions(int minutesAgo) =>
        new FixedAgeExecutions(minutesAgo);

    /// <summary>Mirrors <c>NullRecurringJobLastExecutionSource</c>: always "cannot tell".</summary>
    private sealed class UnknownExecutions : IRecurringJobLastExecutionSource
    {
        public DateTime? GetLastExecutionUtc(string recurringJobId) => null;
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<string> Ids { get; } = new();

        public void AddOrUpdate(
            string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Ids.Add(recurringJobId);

        public void RemoveIfExists(string recurringJobId) { }

        public void Trigger(string recurringJobId) { }
    }
}
