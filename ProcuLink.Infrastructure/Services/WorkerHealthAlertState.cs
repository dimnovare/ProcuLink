using System.Collections.Concurrent;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Process-wide, thread-safe alert state for <see cref="WorkerHealthAlertService"/>. Registered as
/// a SINGLETON so the rate-limit window and the healthy→bad transition flag survive across the
/// recurring (scoped) sweep invocations. Kept separate from the scoped sweep service (which depends
/// on a scoped DbContext via <c>IOpsHealthService</c>) so a singleton never captures a scoped graph.
/// <para>
/// State is kept PER ALERT KEY. That is the point: with one shared window, a worker outage that
/// persists for hours would sit inside its own cooldown and silently swallow the FIRST notification
/// of an unrelated condition — a delivery failure spike or a latched AI cap — for the whole window.
/// Each condition therefore owns its own transition flag and its own last-alert timestamp.
/// </para>
/// </summary>
public sealed class WorkerHealthAlertState
{
    private sealed class ConditionState
    {
        public bool WasBad;
        public DateTime LastAlertUtc = DateTime.MinValue;
    }

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ConditionState> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the current health of ONE condition and decides whether an alert should be emitted
    /// now. Emits on a healthy→bad transition, or once <paramref name="minInterval"/> has elapsed
    /// since that condition's last alert while it is still bad. Recovering to healthy re-arms the
    /// transition alert for that condition only.
    /// </summary>
    /// <param name="alertKey">Condition identifier from <c>OperationalAlertKeys</c>.</param>
    /// <param name="isBad">True when this condition is currently degraded.</param>
    /// <param name="nowUtc">Current time (injected for deterministic tests).</param>
    /// <param name="minInterval">Minimum spacing between repeat alerts while bad.</param>
    /// <returns>True if the caller should emit an alert for this condition now.</returns>
    public bool ShouldAlert(string alertKey, bool isBad, DateTime nowUtc, TimeSpan minInterval)
    {
        var condition = _byKey.GetOrAdd(alertKey, _ => new ConditionState());

        // One gate for every condition. The critical sections are a handful of field reads/writes,
        // and the sweep is single-threaded per run, so per-key locks would buy nothing.
        lock (_gate)
        {
            if (!isBad)
            {
                condition.WasBad = false;
                return false;
            }

            var transition = !condition.WasBad;
            var rateLimitElapsed = nowUtc - condition.LastAlertUtc >= minInterval;
            var shouldAlert = transition || rateLimitElapsed;

            condition.WasBad = true;
            if (shouldAlert)
                condition.LastAlertUtc = nowUtc;

            return shouldAlert;
        }
    }

    /// <summary>Resets every condition to the "never alerted, currently healthy" baseline. Used by tests.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _byKey.Clear();
        }
    }
}
