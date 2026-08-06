using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Reliability/observability — the operator alert sweep (WP-37). Verifies that each of the five
/// conditions fires on its own trigger, that none of them fires on a healthy system, and that the
/// anti-spam rules hold PER CONDITION: one alert on transition, repeats rate-limited, recovery
/// re-arms, and a persistent incident on one condition never gags the first notification of
/// another. The sinks are replaced with a recording fake so no transport runs.
/// </summary>
public class WorkerHealthAlertServiceTests
{
    // ── Condition 1: worker heartbeat loss ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_WorkerDown_RaisesAlert()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: false, ActiveWorkers: 0, SecondsSinceWorkerHeartbeat: 300,
            DeadLetterOrders: 0, FailedDeliveryOrders: 0));

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.WorkerHeartbeatLost);
        sink.Calls[0].Message.Should().Contain("no healthy worker");
    }

    // ── Condition 2: dead-letter backlog ─────────────────────────────────────────

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
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.DeadLetterBacklog);
        sink.Calls[0].Message.Should().Contain("dead-letter+failed deliveries = 25");
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
        sink.Calls.Should().BeEmpty();
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
            health.Object, ProbeReturning(Healthy()).Object, sink, new WorkerHealthAlertState(), options,
            NullLogger<WorkerHealthAlertService>.Instance);

        (await svc.RunAsync(default)).Should().BeTrue();
        sink.Calls.Should().ContainSingle();
    }

    // ── Condition 3: delivery failure-rate spike ─────────────────────────────────

    [Fact]
    public async Task RunAsync_DeliveryFailureRateOverThreshold_RaisesAlert()
    {
        var sink = new RecordingSink();
        // 8 of 10 concluded attempts failed = 80% ≥ 50%, and 10 ≥ the 10-attempt minimum.
        var probe = ProbeReturning(Healthy() with
        {
            DeliveryFailureRate = new DeliveryFailureRateSignal(60, 10, 8),
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.DeliveryFailureRate);
        sink.Calls[0].Message.Should().Contain("8/10");
    }

    [Fact]
    public async Task RunAsync_DeliveryFailureRateHighButSampleTooSmall_DoesNotAlert()
    {
        var sink = new RecordingSink();
        // 100% failure, but only 3 attempts — a spike claim off 3 samples is a false page.
        var probe = ProbeReturning(Healthy() with
        {
            DeliveryFailureRate = new DeliveryFailureRateSignal(60, 3, 3),
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_DeliveryFailureRateBelowThreshold_DoesNotAlert()
    {
        var sink = new RecordingSink();
        // 4 of 100 failed = 4% — normal supplier flakiness, not an incident.
        var probe = ProbeReturning(Healthy() with
        {
            DeliveryFailureRate = new DeliveryFailureRateSignal(60, 100, 4),
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    // ── Condition 4: pull-channel last-success age ───────────────────────────────

    [Fact]
    public async Task RunAsync_PullChannelStale_RaisesAlertNamingTheChannel()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with
        {
            PullChannels = new[] { new PullChannelSignal("email", EnabledOrgs: 2, MinutesSinceLastSuccess: 180) },
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.PullChannelStalled);
        sink.Calls[0].Message.Should().Contain("email");
    }

    [Fact]
    public async Task RunAsync_PullChannelStaleButNobodyUsesIt_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with
        {
            PullChannels = new[] { new PullChannelSignal("sftp", EnabledOrgs: 0, MinutesSinceLastSuccess: 9_999) },
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeFalse("a channel nobody has switched on cannot be a live incident");
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PullChannelNeverSucceeded_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with
        {
            PullChannels = new[] { new PullChannelSignal("s3", EnabledOrgs: 1, MinutesSinceLastSuccess: null) },
        });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeFalse("a channel configured minutes ago has not polled yet");
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PullChannelFresh_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with
        {
            PullChannels = new[] { new PullChannelSignal("email", EnabledOrgs: 3, MinutesSinceLastSuccess: 4) },
        });

        (await CreateService(HealthOk(), sink, probe).RunAsync(default)).Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SeveralStaleChannels_AreReportedInOneAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with
        {
            PullChannels = new[]
            {
                new PullChannelSignal("email", 1, 300),
                new PullChannelSignal("sftp",  1, 400),
                new PullChannelSignal("s3",    1, 2),
            },
        });

        await CreateService(HealthOk(), sink, probe).RunAsync(default);

        sink.Calls.Should().ContainSingle("all stalled channels share one condition and one cooldown");
        sink.Calls[0].Message.Should().Contain("email").And.Contain("sftp");
        sink.Calls[0].Message.Should().NotContain("s3", "a fresh channel must not be named as stalled");
    }

    // ── Condition 5: AI token-cap latch ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AiTokenCapLatched_RaisesAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with { AiTokenLatch = new AiTokenLatchSignal(2) });

        var alerted = await CreateService(HealthOk(), sink, probe).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.AiTokenCapLatched);
        sink.Calls[0].Message.Should().Contain("2");
    }

    [Fact]
    public async Task RunAsync_NoOrgLatched_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var probe = ProbeReturning(Healthy() with { AiTokenLatch = new AiTokenLatchSignal(0) });

        (await CreateService(HealthOk(), sink, probe).RunAsync(default)).Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    // ── All clear ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Healthy_DoesNotAlert()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: true, ActiveWorkers: 2, SecondsSinceWorkerHeartbeat: 3,
            DeadLetterOrders: 1, FailedDeliveryOrders: 1)); // well under threshold

        var alerted = await CreateService(health, sink).RunAsync(default);

        alerted.Should().BeFalse();
        sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_EveryConditionBad_RaisesOneAlertPerCondition()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(
            WorkerHealthy: false, ActiveWorkers: 0, SecondsSinceWorkerHeartbeat: 900,
            DeadLetterOrders: 30, FailedDeliveryOrders: 5));
        var probe = ProbeReturning(new OperationalAlertSignals(
            new DeliveryFailureRateSignal(60, 40, 39),
            new[] { new PullChannelSignal("email", 1, 500) },
            new AiTokenLatchSignal(1)));

        await CreateService(health, sink, probe).RunAsync(default);

        sink.Calls.Select(c => c.Key).Should().BeEquivalentTo(OperationalAlertKeys.All);
    }

    // ── Anti-spam ────────────────────────────────────────────────────────────────

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
            health.Object, ProbeReturning(Healthy()).Object, sink, state, options,
            NullLogger<WorkerHealthAlertService>.Instance, () => now);

        // First bad run → transition alert.
        (await svc.RunAsync(default)).Should().BeTrue();
        // 10 min later, still bad, inside the 30-min window → suppressed.
        now = now.AddMinutes(10);
        (await svc.RunAsync(default)).Should().BeFalse();
        // 35 min after the first alert → window elapsed → alerts again.
        now = now.AddMinutes(25);
        (await svc.RunAsync(default)).Should().BeTrue();

        sink.Calls.Should().HaveCount(2);
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
            health.Object, ProbeReturning(Healthy()).Object, sink, state, options,
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

        sink.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_OneConditionInCooldown_DoesNotSuppressADifferentCondition()
    {
        var sink = new RecordingSink();
        var now = new DateTime(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc);
        var state = new WorkerHealthAlertState();
        var options = new WorkerHealthAlertOptions { MinAlertIntervalMinutes = 30 };

        var workerDown = new WorkerHealthSnapshot(false, 0, 600, 0, 0);
        var health = HealthReturning(workerDown);

        var signals = Healthy();
        var probe = new Mock<IOperationalAlertProbe>();
        probe.Setup(p => p.GetSignalsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(() => signals);

        var svc = new WorkerHealthAlertService(
            health.Object, probe.Object, sink, state, options,
            NullLogger<WorkerHealthAlertService>.Instance, () => now);

        // Worker has been down since 09:00 and alerted once.
        (await svc.RunAsync(default)).Should().BeTrue();
        sink.Calls.Should().ContainSingle();

        // Five minutes later the worker is still down (inside its cooldown) AND the AI cap latches.
        now = now.AddMinutes(5);
        signals = signals with { AiTokenLatch = new AiTokenLatchSignal(1) };

        (await svc.RunAsync(default)).Should().BeTrue();

        sink.Calls.Should().HaveCount(2);
        sink.Calls[1].Key.Should().Be(OperationalAlertKeys.AiTokenCapLatched,
            "each condition owns its own cooldown — a long outage must not gag a new incident");
    }

    // ── Probe resilience ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ProbeThrows_StillEvaluatesTheWorkerAndBacklogConditions()
    {
        var sink = new RecordingSink();
        var health = HealthReturning(new WorkerHealthSnapshot(false, 0, 600, 0, 0));
        var probe = new Mock<IOperationalAlertProbe>();
        probe.Setup(p => p.GetSignalsAsync(It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("probe exploded"));

        var alerted = await CreateService(health, sink, probe).RunAsync(default);

        alerted.Should().BeTrue();
        sink.Calls.Should().ContainSingle();
        sink.Calls[0].Key.Should().Be(OperationalAlertKeys.WorkerHeartbeatLost,
            "a broken probe must degrade the three extra conditions, not the whole sweep");
    }

    [Fact]
    public async Task RunAsync_SinkThrows_DoesNotAbortTheRemainingConditions()
    {
        var throwingSink = new ThrowingOnFirstCallSink();
        var health = HealthReturning(new WorkerHealthSnapshot(false, 0, 600, 0, 0));
        var probe = ProbeReturning(Healthy() with { AiTokenLatch = new AiTokenLatchSignal(3) });

        var svc = new WorkerHealthAlertService(
            health.Object, probe.Object, throwingSink, new WorkerHealthAlertState(),
            new WorkerHealthAlertOptions(), NullLogger<WorkerHealthAlertService>.Instance);

        var act = async () => await svc.RunAsync(default);

        await act.Should().NotThrowAsync();
        throwingSink.Attempts.Should().Be(2,
            "the second condition must still be offered to the sink after the first throw");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static OperationalAlertSignals Healthy() => new(
        new DeliveryFailureRateSignal(60, 0, 0),
        Array.Empty<PullChannelSignal>(),
        new AiTokenLatchSignal(0));

    private static Mock<IOpsHealthService> HealthReturning(WorkerHealthSnapshot snap)
    {
        var health = new Mock<IOpsHealthService>();
        health.Setup(h => h.GetWorkerHealthSnapshotAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(snap);
        return health;
    }

    private static Mock<IOpsHealthService> HealthOk() =>
        HealthReturning(new WorkerHealthSnapshot(true, 1, 3, 0, 0));

    private static Mock<IOperationalAlertProbe> ProbeReturning(OperationalAlertSignals signals)
    {
        var probe = new Mock<IOperationalAlertProbe>();
        probe.Setup(p => p.GetSignalsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(signals);
        return probe;
    }

    private static WorkerHealthAlertService CreateService(
        Mock<IOpsHealthService> health,
        IWorkerAlertSink sink,
        Mock<IOperationalAlertProbe>? probe = null) =>
        new(health.Object, (probe ?? ProbeReturning(Healthy())).Object, sink,
            new WorkerHealthAlertState(), new WorkerHealthAlertOptions(),
            NullLogger<WorkerHealthAlertService>.Instance);

    private sealed class RecordingSink : IWorkerAlertSink
    {
        public List<(string Key, string Message)> Calls { get; } = new();

        public Task AlertAsync(string alertKey, string message, CancellationToken ct = default)
        {
            Calls.Add((alertKey, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOnFirstCallSink : IWorkerAlertSink
    {
        public int Attempts { get; private set; }

        public Task AlertAsync(string alertKey, string message, CancellationToken ct = default)
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("simulated sink failure");
            return Task.CompletedTask;
        }
    }
}
