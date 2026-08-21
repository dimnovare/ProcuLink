using System.Collections.Concurrent;
using ProcuLink.Core.Services.Alerting;

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
/// <para>
/// <b>The state is DURABLE, and a singleton alone was not enough.</b> A singleton survives sweeps
/// inside one process and nothing else, so every Worker restart re-armed every condition and
/// restarted every cooldown. Live evidence, 2026-08-20: alerts at 13:45 / 14:10 / 14:40 (the
/// 30-minute cooldown holding), then 14:50 / 14:55 / 15:00 / 15:05 — the raw 5-minute sweep
/// interval — across a run of Railway redeploys, then 30-minute spacing again once the deploys
/// stopped. A crash-looping Worker floods exactly when its alerts matter most. The in-memory
/// dictionary is now a per-sweep working copy hydrated from <see cref="IWorkerHealthAlertStateStore"/>;
/// the store is the source of truth.
/// </para>
/// </summary>
public sealed class WorkerHealthAlertState
{
    private sealed class ConditionState
    {
        public bool WasBad;
        public DateTime? LastAlertUtc;

        /// <summary>Set when this sweep changed the row, so only changed rows are written back.</summary>
        public bool Dirty;
    }

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ConditionState> _byKey = new(StringComparer.Ordinal);
    private readonly IWorkerHealthAlertStateStore _store;

    /// <summary>
    /// True when the last write-back threw. Carried into the NEXT sweep's degraded report, because
    /// a failed persist is discovered after that sweep's conditions were already assembled — and it
    /// means the very thing this class exists to prevent: the next restart will re-arm.
    /// </summary>
    private bool _lastPersistFailed;

    /// <summary>
    /// Test/default constructor — process-local state with no durability, i.e. the pre-fix
    /// behaviour. Production resolves the database-backed store through DI.
    /// </summary>
    public WorkerHealthAlertState() : this(new InMemoryWorkerHealthAlertStateStore()) { }

    public WorkerHealthAlertState(IWorkerHealthAlertStateStore store) => _store = store;

    /// <summary>
    /// Hydrates the working copy from the durable store at the start of one sweep.
    /// <para>
    /// <b>Never fails closed and silently.</b> If the store cannot be read, the last known
    /// in-memory state is kept — a best-effort cooldown is better than either extreme — and a blind
    /// source string is returned so the caller folds it into
    /// <see cref="OperationalAlertKeys.AlertSweepDegraded"/> like any other unreadable input.
    /// Deliberately NOT the other choice available here: refusing to alert while the store is
    /// unreadable would trade a flood for silence, and silence is the failure direction that gets
    /// an outage missed.
    /// </para>
    /// </summary>
    /// <returns><c>null</c> when the state is trustworthy, otherwise the blind-source description.</returns>
    public async Task<string?> BeginSweepAsync(CancellationToken ct)
    {
        var persistFailed = _lastPersistFailed;
        _lastPersistFailed = false;

        IReadOnlyList<WorkerHealthAlertConditionState> stored;
        try
        {
            stored = await _store.LoadAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoadFailure = ex;
            return "the alert cooldown store (per-condition alert spacing; it cannot be confirmed "
                 + "that this run's alerts respect their cooldown, and a restart may re-alert)";
        }

        LoadFailure = null;

        lock (_gate)
        {
            foreach (var row in stored)
            {
                var condition = _byKey.GetOrAdd(row.AlertKey, _ => new ConditionState());
                condition.WasBad = row.WasBad;
                condition.LastAlertUtc = row.LastAlertUtc;
                condition.Dirty = false;
            }
        }

        return persistFailed
            ? "the alert cooldown store (the previous sweep could not write back its cooldowns, so "
            + "a restart before the next successful write will re-alert)"
            : null;
    }

    /// <summary>The exception from the last failed <see cref="BeginSweepAsync"/>, for logging.</summary>
    public Exception? LoadFailure { get; private set; }

    /// <summary>
    /// Records the current health of ONE condition and decides whether an alert should be emitted
    /// now. Emits on a healthy→bad transition, or once <paramref name="minInterval"/> has elapsed
    /// since that condition's last alert while it is still bad. Recovering to healthy re-arms the
    /// transition alert for that condition only.
    /// <para>
    /// Decides against the working copy hydrated by <see cref="BeginSweepAsync"/>;
    /// <see cref="CommitSweepAsync"/> writes the changes back.
    /// </para>
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
                condition.Dirty |= condition.WasBad;
                condition.WasBad = false;
                return false;
            }

            var transition = !condition.WasBad;
            // A condition that has never alerted has no window to sit inside.
            var rateLimitElapsed = condition.LastAlertUtc is not { } last
                                || nowUtc - last >= minInterval;
            var shouldAlert = transition || rateLimitElapsed;

            // Nothing changes on a suppressed repeat: WasBad is already true and the timestamp is
            // untouched, so there is nothing to write back.
            condition.Dirty |= shouldAlert;
            condition.WasBad = true;
            if (shouldAlert)
                condition.LastAlertUtc = nowUtc;

            return shouldAlert;
        }
    }

    /// <summary>
    /// Writes back every condition this sweep changed.
    /// <para>
    /// A failure here is remembered rather than thrown: the sweep has already delivered its alerts,
    /// and taking the job down would not un-send them. The next <see cref="BeginSweepAsync"/>
    /// reports it as a blind source, so the operator learns the cooldown is not being persisted.
    /// </para>
    /// </summary>
    /// <returns>The exception if the write-back failed, otherwise <c>null</c>.</returns>
    public async Task<Exception?> CommitSweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        List<WorkerHealthAlertConditionState> changed;
        lock (_gate)
        {
            changed = _byKey
                .Where(kv => kv.Value.Dirty)
                .Select(kv => new WorkerHealthAlertConditionState(
                    kv.Key, kv.Value.WasBad, kv.Value.LastAlertUtc))
                .ToList();
        }

        if (changed.Count == 0)
            return null;

        try
        {
            await _store.SaveAsync(changed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastPersistFailed = true;
            return ex;
        }

        lock (_gate)
        {
            foreach (var key in changed)
                if (_byKey.TryGetValue(key.AlertKey, out var condition))
                    condition.Dirty = false;
        }

        return null;
    }

    /// <summary>Resets every condition to the "never alerted, currently healthy" baseline. Used by tests.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _byKey.Clear();
            _lastPersistFailed = false;
            LoadFailure = null;
        }
    }
}
