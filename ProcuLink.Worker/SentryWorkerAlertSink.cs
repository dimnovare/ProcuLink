using ProcuLink.Core.Services;
using Sentry;

namespace ProcuLink.Worker;

/// <summary>
/// <see cref="IWorkerAlertSink"/> backed by Sentry. Sentry is initialised in
/// <c>Program.cs</c> via <c>ILoggingBuilder.AddSentry</c>; when <c>Sentry:Dsn</c> is empty the
/// SDK initialises disabled, so <see cref="SentrySdk.CaptureMessage(string, SentryLevel)"/> is a
/// silent no-op. We additionally guard on <see cref="SentrySdk.IsEnabled"/> to avoid even
/// building the event when alerting is off.
/// <para>
/// A disabled SDK reports <c>false</c> rather than <c>true</c>: <c>Sentry:Dsn</c> ships empty in
/// <c>appsettings.Production.json</c>, so "the SDK accepted the call" is not evidence that anything
/// left the process. The composite needs the honest answer to tell the sweep whether ANY transport
/// reached the operator.
/// </para>
/// </summary>
public sealed class SentryWorkerAlertSink : IWorkerAlertSink
{
    public Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default)
    {
        if (!SentrySdk.IsEnabled)
            return Task.FromResult(false);

        SentrySdk.CaptureMessage($"[{alertKey}] {message}", SentryLevel.Error);
        return Task.FromResult(true);
    }
}
