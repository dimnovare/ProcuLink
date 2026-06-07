namespace ProcuLink.Core.Services;

/// <summary>
/// Tunables for the recurring worker-health alert sweep. Bound from configuration section
/// <c>WorkerHealthAlert</c> where present; otherwise the defaults below apply. Safe defaults:
/// alerting is always active (it only emits through a sink that is itself a no-op without a
/// Sentry DSN), the dead-letter threshold is conservative, and the rate-limit prevents spam.
/// </summary>
public sealed class WorkerHealthAlertOptions
{
    public const string SectionName = "WorkerHealthAlert";

    /// <summary>
    /// All-org dead-letter + failed-delivery order count at/above which an alert fires.
    /// Default 25. A non-positive configured value falls back to the default.
    /// </summary>
    public int DeadLetterThreshold { get; set; } = 25;

    /// <summary>
    /// While health stays bad, re-alert no more often than this many minutes (avoids alert spam).
    /// Default 30. A non-positive configured value falls back to the default.
    /// </summary>
    public int MinAlertIntervalMinutes { get; set; } = 30;

    /// <summary>Effective dead-letter threshold (never non-positive).</summary>
    public int EffectiveDeadLetterThreshold => DeadLetterThreshold > 0 ? DeadLetterThreshold : 25;

    /// <summary>Effective rate-limit interval (never non-positive).</summary>
    public TimeSpan MinAlertInterval =>
        TimeSpan.FromMinutes(MinAlertIntervalMinutes > 0 ? MinAlertIntervalMinutes : 30);
}
