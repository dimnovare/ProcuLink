using FluentAssertions;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Tests for the worker-heartbeat health snapshot of <see cref="OpsHealthService"/>.
/// The monitoring API is faked via <see cref="IMonitoringApi"/> — no real Hangfire storage needed.
/// </summary>
public class OpsWorkerHealthTests
{
    private static ServerDto MakeServer(DateTime? heartbeat) =>
        new()
        {
            Name         = "test-worker:1",
            WorkersCount = 10,
            Queues       = new List<string> { "default" },
            StartedAt    = DateTime.UtcNow.AddMinutes(-5),
            Heartbeat    = heartbeat,
        };

    // ── workerHealthy = true when heartbeat is recent ────────────────────────

    [Fact]
    public async Task GetHealth_RecentHeartbeat_WorkerHealthyTrue()
    {
        var recentHeartbeat  = DateTime.UtcNow.AddSeconds(-10); // well within 60 s

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers())
                  .Returns(new List<ServerDto> { MakeServer(recentHeartbeat) });

        var svc = new OpsHealthService(monitoring.Object);
        var s   = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        s.WorkerHealthy.Should().BeTrue();
        s.ActiveWorkers.Should().Be(1);
        s.SecondsSinceWorkerHeartbeat.Should().BeInRange(5, 30);
        s.SecondsSinceWorkerHeartbeat.Should().BeGreaterThan(0).And.BeLessThan(60);
    }

    // ── workerHealthy = false when heartbeat is stale ─────────────────────────

    [Fact]
    public async Task GetHealth_StaleHeartbeat_WorkerHealthyFalse()
    {
        var staleHeartbeat   = DateTime.UtcNow.AddMinutes(-5); // 5 min old — outside 60 s window

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers())
                  .Returns(new List<ServerDto> { MakeServer(staleHeartbeat) });

        var svc = new OpsHealthService(monitoring.Object);
        var s   = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        s.WorkerHealthy.Should().BeFalse();
        s.ActiveWorkers.Should().Be(1);
        s.SecondsSinceWorkerHeartbeat.Should().BeGreaterThan(60);
    }

    // ── workerHealthy = false when no servers registered ─────────────────────

    [Fact]
    public async Task GetHealth_NoServers_WorkerHealthyFalse_NullHeartbeat()
    {

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers())
                  .Returns(new List<ServerDto>());

        var svc = new OpsHealthService(monitoring.Object);
        var s   = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        s.WorkerHealthy.Should().BeFalse();
        s.ActiveWorkers.Should().Be(0);
        s.SecondsSinceWorkerHeartbeat.Should().BeNull();
    }

    // ── workerHealthy = false when monitoring API is null (no storage) ────────

    [Fact]
    public async Task GetHealth_NullMonitoringApi_WorkerHealthyFalse()
    {

        // No monitoring API injected (test env without real JobStorage.Current).
        var svc = new OpsHealthService(monitoring: null);
        var s   = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        s.WorkerHealthy.Should().BeFalse();
        s.ActiveWorkers.Should().Be(0);
        s.SecondsSinceWorkerHeartbeat.Should().BeNull();
    }

    // ── workerHealthy = false when monitoring API throws ─────────────────────

    [Fact]
    public async Task GetHealth_MonitoringApiThrows_WorkerHealthyFalse_NoException()
    {

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers())
                  .Throws<InvalidOperationException>();

        var svc = new OpsHealthService(monitoring.Object);
        var act = () => svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        // Must not throw — the endpoint must remain available when storage is broken.
        await act.Should().NotThrowAsync();

        var s = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);
        s.WorkerHealthy.Should().BeFalse();
    }

    // ── highest heartbeat wins when multiple servers are registered ───────────

    [Fact]
    public async Task GetHealth_MultipleServers_MostRecentHeartbeatUsed()
    {
        var oldest         = DateTime.UtcNow.AddMinutes(-10);
        var newest         = DateTime.UtcNow.AddSeconds(-5);

        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers())
                  .Returns(new List<ServerDto>
                  {
                      MakeServer(oldest),
                      MakeServer(newest),
                  });

        var svc = new OpsHealthService(monitoring.Object);
        var s   = await svc.GetWorkerHealthSnapshotAsync(CancellationToken.None);

        s.ActiveWorkers.Should().Be(2);
        s.SecondsSinceWorkerHeartbeat.Should().BeInRange(0, 30);
        s.WorkerHealthy.Should().BeTrue(); // newest is within 60 s
    }
}
