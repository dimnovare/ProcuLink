using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// Fans one alert out to every configured transport, isolating each. Alerting is the last line of
/// defence, so a transport that is down (Sentry rate-limited, Postmark refusing) must not stop the
/// others from delivering the same alert — every sink is invoked inside its own try/catch and the
/// composite itself never throws.
/// </summary>
public sealed class CompositeWorkerAlertSink : IWorkerAlertSink
{
    private readonly IReadOnlyList<IWorkerAlertSink> _sinks;
    private readonly ILogger<CompositeWorkerAlertSink> _logger;

    public CompositeWorkerAlertSink(
        IEnumerable<IWorkerAlertSink> sinks,
        ILogger<CompositeWorkerAlertSink> logger)
    {
        _sinks = sinks.ToList();
        _logger = logger;
    }

    public async Task AlertAsync(string alertKey, string message, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.AlertAsync(alertKey, message, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Alert sink {Sink} failed for {AlertKey}; continuing with the remaining sinks.",
                    sink.GetType().Name, alertKey);
            }
        }
    }
}
