using FluentAssertions;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ProcuLink.Api.Controllers;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Tests for <see cref="WorkerHeartbeatHealthCheck"/> — the readiness check that
/// reads Hangfire's shared-storage server heartbeat to detect a dead/stale Worker.
/// The monitoring API is faked via <see cref="IMonitoringApi"/> (no real storage),
/// mirroring <see cref="OpsWorkerHealthTests"/>.
/// </summary>
public class WorkerHeartbeatHealthCheckTests
{
    private static ServerDto MakeServer(DateTime? heartbeat) =>
        new()
        {
            Name = "test-worker:1",
            WorkersCount = 10,
            Queues = new List<string> { "default" },
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            Heartbeat = heartbeat,
        };

    private static IMonitoringApi WithServers(params ServerDto[] servers)
    {
        var m = new Mock<IMonitoringApi>();
        m.Setup(x => x.Servers()).Returns(servers.ToList());
        return m.Object;
    }

    private static async Task<HealthCheckResult> RunAsync(IMonitoringApi? monitoring)
    {
        var check = new WorkerHeartbeatHealthCheck(monitoring);
        return await check.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None);
    }

    // ── Healthy: a recent heartbeat ──────────────────────────────────────────

    [Fact]
    public async Task RecentHeartbeat_ReportsHealthy_WithWorkerCount()
    {
        var monitoring = WithServers(MakeServer(DateTime.UtcNow.AddSeconds(-10)));

        var result = await RunAsync(monitoring);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeWorkers"].Should().Be(1);
        result.Data.Should().ContainKey("secondsSinceWorkerHeartbeat");
    }

    // ── Degraded (NOT Unhealthy): a stale heartbeat ──────────────────────────

    [Fact]
    public async Task StaleHeartbeat_ReportsDegraded_NotUnhealthy()
    {
        // 5 min old — well past the 60 s deadline.
        var monitoring = WithServers(MakeServer(DateTime.UtcNow.AddMinutes(-5)));

        var result = await RunAsync(monitoring);

        // Degraded keeps /health/ready at HTTP 200 — the JSON flag carries the alert.
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("stale");
        ((double)result.Data["secondsSinceWorkerHeartbeat"]).Should().BeGreaterThan(60);
    }

    // ── Degraded: no worker registered at all ────────────────────────────────

    [Fact]
    public async Task NoServersRegistered_ReportsDegraded_ZeroWorkers()
    {
        var monitoring = WithServers(); // empty

        var result = await RunAsync(monitoring);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["activeWorkers"].Should().Be(0);
        result.Description.Should().Contain("No Hangfire worker");
    }

    // ── Degraded + no throw: null monitoring API (test env / storage not ready)

    [Fact]
    public async Task NullMonitoringApi_ReportsDegraded_NoException()
    {
        var act = async () => await RunAsync(monitoring: null);

        await act.Should().NotThrowAsync();
        var result = await RunAsync(monitoring: null);
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    // ── Degraded + no throw: monitoring API throws (broken storage) ───────────

    [Fact]
    public async Task MonitoringApiThrows_ReportsDegraded_NoException()
    {
        var m = new Mock<IMonitoringApi>();
        m.Setup(x => x.Servers()).Throws<InvalidOperationException>();

        var act = async () => await RunAsync(m.Object);

        // The readiness endpoint must never fail because Hangfire storage blips.
        await act.Should().NotThrowAsync();
        (await RunAsync(m.Object)).Status.Should().Be(HealthStatus.Degraded);
    }

    // ── Most-recent heartbeat wins across multiple servers ───────────────────

    [Fact]
    public async Task MultipleServers_UsesMostRecentHeartbeat()
    {
        var monitoring = WithServers(
            MakeServer(DateTime.UtcNow.AddMinutes(-10)), // stale
            MakeServer(DateTime.UtcNow.AddSeconds(-5)));  // fresh

        var result = await RunAsync(monitoring);

        // The freshest heartbeat is within 60 s → Healthy, 2 workers registered.
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeWorkers"].Should().Be(2);
    }

    // ── Data bag never carries secrets (only counts/ages/deadline) ───────────

    [Fact]
    public async Task DataBag_OnlyContainsNonSecretDiagnostics()
    {
        var monitoring = WithServers(MakeServer(DateTime.UtcNow.AddSeconds(-3)));

        var result = await RunAsync(monitoring);

        result.Data.Keys.Should().OnlyContain(k =>
            k == "activeWorkers" ||
            k == "secondsSinceWorkerHeartbeat" ||
            k == "heartbeatDeadlineSeconds");
    }
}
