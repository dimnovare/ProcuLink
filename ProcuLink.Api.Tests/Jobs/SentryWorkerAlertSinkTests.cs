using FluentAssertions;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Worker;
using Sentry;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// The Sentry transport's honesty about whether it delivered anything.
///
/// <para><b>Why this needs a test at all.</b> <c>ProcuLink.Worker/appsettings.Production.json</c>
/// ships <c>"Sentry": { "Dsn": "" }</c>, and with an empty DSN the SDK initialises DISABLED — so
/// this sink's normal production state may well be "accepts every call, transmits nothing". If it
/// reported that as a successful delivery, the composite would report the same, and the sweep would
/// log that it raised an alert nobody could have received.</para>
///
/// <para>The assertion below is the only thing standing between a one-character edit
/// (<c>Task.FromResult(false)</c> → <c>true</c>) and exactly that silence.</para>
/// </summary>
public sealed class SentryWorkerAlertSinkTests
{
    [Fact]
    public async Task AlertAsync_withTheSdkDisabled_reportsNotDelivered()
    {
        // Anti-vacuity: this asserts the DISABLED path, so the test is meaningless if some other
        // test in this assembly has initialised the SDK. Nothing does today; fail loudly if that
        // ever changes rather than passing for the wrong reason.
        SentrySdk.IsEnabled.Should().BeFalse(
            "this test asserts the no-DSN behaviour and cannot observe it once Sentry is enabled");

        var delivered = await new SentryWorkerAlertSink()
            .AlertAsync(OperationalAlertKeys.WorkerHeartbeatLost, "no healthy worker");

        delivered.Should().BeFalse(
            "an empty Sentry:Dsn means the message never left the process — reporting that as a "
          + "delivered alert is how the sweep ends up claiming it notified somebody");
    }
}
