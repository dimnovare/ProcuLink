namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Process-wide, thread-safe alert state for <see cref="WorkerHealthAlertService"/>. Registered as
/// a SINGLETON so the rate-limit window and the healthy→bad transition flag survive across the
/// recurring (scoped) sweep invocations. Kept separate from the scoped sweep service (which depends
/// on a scoped DbContext via <c>IOpsHealthService</c>) so a singleton never captures a scoped graph.
/// </summary>
public sealed class WorkerHealthAlertState
{
    private readonly object _gate = new();
    private bool _wasBad;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    /// <summary>
    /// Records the current health and decides whether an alert should be emitted now.
    /// Emits on a healthy→bad transition, or once <paramref name="minInterval"/> has elapsed since
    /// the last alert while still bad. Recovering to healthy re-arms the transition alert.
    /// </summary>
    /// <param name="isBad">True when health is currently degraded.</param>
    /// <param name="nowUtc">Current time (injected for deterministic tests).</param>
    /// <param name="minInterval">Minimum spacing between repeat alerts while bad.</param>
    /// <returns>True if the caller should emit an alert this run.</returns>
    public bool ShouldAlert(bool isBad, DateTime nowUtc, TimeSpan minInterval)
    {
        lock (_gate)
        {
            if (!isBad)
            {
                _wasBad = false;
                return false;
            }

            var transition = !_wasBad;
            var rateLimitElapsed = nowUtc - _lastAlertUtc >= minInterval;
            var shouldAlert = transition || rateLimitElapsed;

            _wasBad = true;
            if (shouldAlert)
                _lastAlertUtc = nowUtc;

            return shouldAlert;
        }
    }

    /// <summary>Resets to the "never alerted, currently healthy" baseline. Used by tests.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _wasBad = false;
            _lastAlertUtc = DateTime.MinValue;
        }
    }
}
