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

    /// <summary>
    /// The transports this composite was actually constructed with, in fan-out order.
    /// <para>
    /// Exposed so the host wiring can be verified against the CONSTRUCTED object graph. The previous
    /// guard matched <c>Program.cs</c> source with a regex that stopped at the constructor name, so
    /// deleting a transport from the argument list left every test green while removing the routing
    /// the alerting packet exists to deliver.
    /// </para>
    /// </summary>
    public IReadOnlyList<IWorkerAlertSink> Sinks => _sinks;

    /// <summary>
    /// Fans out to every transport and reports whether ANY of them actually delivered. One working
    /// transport is enough to notify the operator; zero means the alert left the process through
    /// nothing at all, which the caller must not mistake for a raised alert.
    /// </summary>
    public async Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct = default)
    {
        var deliveredByAny = false;

        foreach (var sink in _sinks)
        {
            try
            {
                deliveredByAny |= await sink.AlertAsync(alertKey, message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Alert sink {Sink} failed for {AlertKey}; continuing with the remaining sinks.",
                    sink.GetType().Name, alertKey);
            }
        }

        return deliveredByAny;
    }
}
