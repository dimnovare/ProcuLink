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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static CompositeWorkerAlertSink Composite(params IWorkerAlertSink[] sinks) =>
        new(sinks, NullLogger<CompositeWorkerAlertSink>.Instance);

    private sealed class RecordingSink : IWorkerAlertSink
    {
        public List<(string Key, string Message)> Calls { get; } = new();

        public Task AlertAsync(string alertKey, string message, CancellationToken ct = default)
        {
            Calls.Add((alertKey, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IWorkerAlertSink
    {
        public Task AlertAsync(string alertKey, string message, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated sink failure");
    }
}
