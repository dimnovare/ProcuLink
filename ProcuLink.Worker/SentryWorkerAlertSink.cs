using ProcuLink.Core.Services;
using Sentry;

namespace ProcuLink.Worker;

/// <summary>
/// <see cref="IWorkerAlertSink"/> backed by Sentry. Sentry is initialised in
/// <c>Program.cs</c> via <c>ILoggingBuilder.AddSentry</c>; when <c>Sentry:Dsn</c> is empty the
/// SDK initialises disabled, so <see cref="SentrySdk.CaptureMessage(string, SentryLevel)"/> is a
/// silent no-op. We additionally guard on <see cref="SentrySdk.IsEnabled"/> to avoid even
/// building the event when alerting is off.
/// </summary>
public sealed class SentryWorkerAlertSink : IWorkerAlertSink
{
    public void Alert(string message)
    {
        if (!SentrySdk.IsEnabled)
            return;

        SentrySdk.CaptureMessage(message, SentryLevel.Error);
    }
}
