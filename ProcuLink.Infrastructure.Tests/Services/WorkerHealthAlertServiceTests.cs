using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Reliability/observability — worker-health alert sweep. Verifies that the alert path fires when
/// the worker is down or the dead-letter/failed backlog crosses the threshold, that it does NOT
/// fire when healthy, and that anti-spam works (one alert on transition, repeats rate-limited,
/// recovery re-arms). The Sentry sink is replaced with a recording fake so no transport runs.
/// </summary>
public class WorkerHealthAlertServiceTests
{
    [Fact]
    public async Task RunAsync_WorkerDown_RaisesAlert()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: false, ActiveWorkers: 0, SecondsSinceWorkerHeartbeat: 300,
            DeadLetterOrders: 0, FailedDeliveryOrders: 0));

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Messages.Should().ContainSingle();
        sink.Messages[0].Should().Contain("no healthy worker");
    }

    [Fact]
    public async Task RunAsync_DeadLetterBacklogAtThreshold_RaisesAlert()
    {
        var sink = new RecordingSink();
        // Healthy worker, but dead-letter + failed crosses the threshold (default 25).
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: true, ActiveWorkers: 1, SecondsSinceWorkerHeartbeat: 5,
            DeadLetterOrders: 20, FailedDeliveryOrders: 5));

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Messages.Should().ContainSingle();
        sink.Messages[0].Should().Contain("dead-letter+failed deliveries = 25");
    }

    [Fact]
    public async Task RunAsync_Healthy_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: true, ActiveWorkers: 2, SecondsSinceWorkerHeartbeat: 3,
            DeadLetterOrders: 1, FailedDeliveryOrders: 1)); // well under threshold

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeFalse();
        sink.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_BelowThresholdAndWorkerUp_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: true, ActiveWorkers: 1, SecondsSinceWorkerHeartbeat: 2,
            DeadLetterOrders: 12, FailedDeliveryOrders: 12)); // 24 < 25 threshold

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeFalse();
        sink.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_StaysBadWithinWindow_AlertsOnceThenSuppresses()
    {
        var sink = new RecordingSink();
        var bad = new WorkerHealthSnapshot(
            WorkerHealthy: false, ActiveWorkers: 0, SecondsSinceWorkerHeartbeat: 120,
            DeadLetterOrders: 0, FailedDeliveryOrders: 0);
        var health = HealthReturning(bad);

        var now = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
        var state = new WorkerHealthAlertState();
        var options = new WorkerHealthAlertOptions { MinAlertIntervalMinutes = 30 };

        var svc = new WorkerHealthAlertService(
            health.Object, sink, state, options,
            NullLogger<WorkerHealthAlertService>.Instance, () => now);

        // First bad run → transition alert.
        (await svc.RunAsync(default)).Should().BeTrue();
        // 10 min later, still bad, inside the 30-min window → suppressed.
        now = now.AddMinutes(10);
        (await svc.RunAsync(default)).Should().BeFalse();
        // 35 min after the first alert → window elapsed → alerts again.
        now = now.AddMinutes(25);
        (await svc.RunAsync(default)).Should().BeTrue();

        sink.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_RecoveryReArmsTransitionAlert()
    {
        var sink = new RecordingSink();
        var state = new WorkerHealthAlertState();
        var options = new WorkerHealthAlertOptions { MinAlertIntervalMinutes = 30 };
        var now = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

        var bad = new WorkerHealthSnapshot(false, 0, 120, 0, 0);
        var good = new WorkerHealthSnapshot(true, 1, 2, 0, 0);

        var seq = new Queue<WorkerHealthSnapshot>(new[] { bad, good, bad });
        var health = new Mock<IOpsHealthService>();
        health.Setup(h => h.GetWorkerHealthSnapshotAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => seq.Dequeue());

        var svc = new WorkerHealthAlertService(
            health.Object, sink, state, options,
            NullLogger<WorkerHealthAlertService>.Instance, () => now);

        // bad → alert
        (await svc.RunAsync(default)).Should().BeTrue();
        // good → no alert, re-arms transition
        now = now.AddMinutes(5);
        (await svc.RunAsync(default)).Should().BeFalse();
        // bad again, only 5 more minutes later (inside the rate-limit window) → still alerts
        // because recovery re-armed the transition.
        now = now.AddMinutes(5);
        (await svc.RunAsync(default)).Should().BeTrue();

        sink.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_CustomThreshold_IsRespected()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: true, ActiveWorkers: 1, SecondsSinceWorkerHeartbeat: 2,
            DeadLetterOrders: 5, FailedDeliveryOrders: 0));
        var options = new WorkerHealthAlertOptions { DeadLetterThreshold = 5 };

        var svc = new WorkerHealthAlertService(
            health.Object, sink, new WorkerHealthAlertState(), options,
            NullLogger<WorkerHealthAlertService>.Instance);

        (await svc.RunAsync(default)).Should().BeTrue();
        sink.Messages.Should().ContainSingle();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Mock<IOpsHealthService> HealthReturning(WorkerHealthSnapshot snap)
    {
        var health = new Mock<IOpsHealthService>();
        health.Setup(h => h.GetWorkerHealthSnapshotAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(snap);
        return health;
    }

    private static WorkerHealthAlertService CreateService(Mock<IOpsHealthService> health, IWorkerAlertSink sink) =>
        new(health.Object, sink, new WorkerHealthAlertState(), new WorkerHealthAlertOptions(),
            NullLogger<WorkerHealthAlertService>.Instance);

    private sealed class RecordingSink : IWorkerAlertSink
    {
        public List<string> Messages { get; } = new();
        public void Alert(string message) => Messages.Add(message);
    }
}
