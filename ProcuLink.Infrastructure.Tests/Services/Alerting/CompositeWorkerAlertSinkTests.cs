using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services.Alerting;

namespace ProcuLink.Infrastructure.Tests.Services.Alerting;

/// <summary>
/// WP-37 — fan-out to every configured alert transport. The point of the composite is ISOLATION:
/// alerting is the last line of defence, so one broken transport (Sentry rate-limited, Postmark
/// down) must not stop the others from delivering the same alert.
/// </summary>
public class CompositeWorkerAlertSinkTests
{
    [Fact]
    public async Task AlertAsync_DeliversToEverySink()
    {
        var a = new RecordingSink();
        var b = new RecordingSink();
        var composite = Composite(a, b);

        await composite.AlertAsync(OperationalAlertKeys.WorkerHeartbeatLost, "worker down");

        a.Calls.Should().Equal((OperationalAlertKeys.WorkerHeartbeatLost, "worker down"));
        b.Calls.Should().Equal((OperationalAlertKeys.WorkerHeartbeatLost, "worker down"));
    }

    [Fact]
    public async Task AlertAsync_OneSinkThrows_TheOthersStillReceiveTheAlert()
    {
        var good = new RecordingSink();
        var composite = Composite(new ThrowingSink(), good);

        var act = async () => await composite.AlertAsync(OperationalAlertKeys.DeadLetterBacklog, "backlog");

        await act.Should().NotThrowAsync();
        good.Calls.Should().ContainSingle(
            "a failing transport must not suppress a working one");
    }

    [Fact]
    public async Task AlertAsync_LastSinkThrows_StillDoesNotEscape()
    {
        var good = new RecordingSink();
        var composite = Composite(good, new ThrowingSink());

        var act = async () => await composite.AlertAsync(OperationalAlertKeys.AiTokenCapLatched, "latched");

        await act.Should().NotThrowAsync();
        good.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task AlertAsync_NoSinksConfigured_IsASilentNoOp()
    {
        var composite = Composite();

        var act = async () => await composite.AlertAsync(OperationalAlertKeys.PullChannelStalled, "stale");

        await act.Should().NotThrowAsync();
    }

    // ── Reporting whether the operator was actually reached ──────────────────────

    [Fact]
    public async Task AlertAsync_AtLeastOneTransportDelivered_ReportsDelivered()
    {
        var composite = Composite(new UndeliverableSink(), new RecordingSink());

        var delivered = await composite.AlertAsync(OperationalAlertKeys.WorkerHeartbeatLost, "down");

        delivered.Should().BeTrue("one working transport is enough to notify the operator");
    }

    [Fact]
    public async Task AlertAsync_EveryTransportUnconfigured_ReportsNotDelivered()
    {
        var composite = Composite(new UndeliverableSink(), new UndeliverableSink());

        var delivered = await composite.AlertAsync(OperationalAlertKeys.WorkerHeartbeatLost, "down");

        delivered.Should().BeFalse(
            "every sink is a deliberate no-op when unconfigured, so 'nothing threw' must not be "
          + "reported as 'the operator was told'");
    }

    [Fact]
    public async Task AlertAsync_EveryTransportThrows_ReportsNotDelivered()
    {
        var composite = Composite(new ThrowingSink(), new ThrowingSink());

        var delivered = await composite.AlertAsync(OperationalAlertKeys.DeadLetterBacklog, "backlog");

        delivered.Should().BeFalse();
    }

    [Fact]
    public async Task AlertAsync_NoSinksConfigured_ReportsNotDelivered()
    {
        var delivered = await Composite().AlertAsync(OperationalAlertKeys.PullChannelStalled, "stale");

        delivered.Should().BeFalse("an empty composite reaches nobody");
    }

    [Fact]
    public void Sinks_ExposesWhatTheCompositeWasActuallyBuiltWith()
    {
        var composite = Composite(new RecordingSink(), new ThrowingSink());

        composite.Sinks.Select(s => s.GetType()).Should()
            .Equal(new[] { typeof(RecordingSink), typeof(ThrowingSink) },
                "the wiring guard inspects the constructed graph rather than reading source text");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static CompositeWorkerAlertSink Composite(params IWorkerAlertSink[] sinks) =>
        new(sinks, NullLogger<CompositeWorkerAlertSink>.Instance);

    private sealed class RecordingSink : IWorkerAlertSink
    {
        public List<(string Key, string Message)> Calls { get; } = new();

        public Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default)
        {
            Calls.Add((alertKey, message));
            return Task.FromResult(true);
        }
    }

    /// <summary>Accepts the call, delivers nothing — an unconfigured transport.</summary>
    private sealed class UndeliverableSink : IWorkerAlertSink
    {
        public Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class ThrowingSink : IWorkerAlertSink
    {
        public Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated sink failure");
    }
}
