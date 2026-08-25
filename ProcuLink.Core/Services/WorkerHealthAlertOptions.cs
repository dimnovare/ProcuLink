namespace ProcuLink.Core.Services;

/// <summary>
/// Tunables for the recurring worker-health alert sweep. Bound from configuration section
/// <c>WorkerHealthAlert</c> where present; otherwise the defaults below apply. Safe defaults:
/// alerting is always active (it only emits through sinks that are themselves no-ops without a
/// Sentry DSN / Postmark token / recipient), the thresholds are conservative, and the per-condition
/// rate-limit prevents spam.
/// <para>
/// EVERY threshold here exists to stop a false page. An alert that fires spuriously trains the one
/// person who reads it to ignore all of them, which is strictly worse than no alert at all — so a
/// condition only trips on a level that is unambiguously wrong, and each has its own independent
/// cooldown window.
/// </para>
/// </summary>
public sealed class WorkerHealthAlertOptions
{
    public const string SectionName = "WorkerHealthAlert";

    /// <summary>
    /// All-org dead-letter + failed-delivery order count at/above which an alert fires.
    /// Default 1 — deliberately LOW, for the same pilot-scale reason as
    /// <see cref="PipelineFailureThreshold"/>: a single dead-lettered order already encodes three
    /// concluded delivery failures over ~90 minutes of backoff, so it is unambiguously wrong, not
    /// noise. The old default of 25 was arithmetically unreachable during a pilot — a Pilot org is
    /// capped at 20 orders TOTAL — which meant a dead supplier endpoint alerted nobody. The
    /// anti-false-page rule this file opens with is satisfied by what the counter counts, not by
    /// the threshold: an order only enters this count after retries are exhausted. A non-positive
    /// configured value falls back to the default.
    /// </summary>
    public int DeadLetterThreshold { get; set; } = 1;

    /// <summary>
    /// All-org failed + transform-failed (pre-delivery pipeline) order count at/above which an
    /// alert fires. Default 1 — deliberately LOW, unlike <see cref="DeadLetterThreshold"/>: at
    /// pilot scale a single order stuck in <c>failed</c> or <c>transform_failed</c> means a broken
    /// parser or output template, and these orders reach no other alert condition. A non-positive
    /// configured value falls back to the default.
    /// </summary>
    public int PipelineFailureThreshold { get; set; } = 1;

    /// <summary>
    /// While a given condition stays bad, re-alert no more often than this many minutes (avoids
    /// alert spam). Applied PER CONDITION, so one persistent incident never gags another.
    /// Default 30. A non-positive configured value falls back to the default.
    /// </summary>
    public int MinAlertIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Trailing window (minutes) over which delivery attempts are scored for the failure-rate
    /// condition. Default 60. A non-positive configured value falls back to the default.
    /// </summary>
    public int DeliveryFailureWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum number of CONCLUDED delivery attempts in the window before the failure rate is
    /// allowed to trip at all. This is the anti-false-page guard: one failure out of one attempt is
    /// 100% and means nothing. Default 10. A non-positive configured value falls back to the default.
    /// </summary>
    public int DeliveryFailureMinAttempts { get; set; } = 10;

    /// <summary>
    /// Failure share (percent, 0–100) of concluded attempts at/above which the rate condition trips,
    /// once <see cref="DeliveryFailureMinAttempts"/> is met. Default 50 — half of everything the
    /// product tried to send failed. A value outside 1–100 falls back to the default.
    /// </summary>
    public int DeliveryFailurePercent { get; set; } = 50;

    /// <summary>
    /// Age (minutes) of a pull channel's last observed success at/above which it is treated as
    /// stalled. Default 60 — twelve missed five-minute polls, well clear of one slow cycle.
    /// A non-positive configured value falls back to the default.
    /// </summary>
    public int PullChannelStaleMinutes { get; set; } = 60;

    /// <summary>Effective dead-letter threshold (never non-positive).</summary>
    public int EffectiveDeadLetterThreshold => DeadLetterThreshold > 0 ? DeadLetterThreshold : 1;

    /// <summary>Effective pipeline-failure threshold (never non-positive).</summary>
    public int EffectivePipelineFailureThreshold =>
        PipelineFailureThreshold > 0 ? PipelineFailureThreshold : 1;

    /// <summary>Effective rate-limit interval (never non-positive).</summary>
    public TimeSpan MinAlertInterval =>
        TimeSpan.FromMinutes(MinAlertIntervalMinutes > 0 ? MinAlertIntervalMinutes : 30);

    /// <summary>Effective delivery failure-rate window (never non-positive).</summary>
    public int EffectiveDeliveryFailureWindowMinutes =>
        DeliveryFailureWindowMinutes > 0 ? DeliveryFailureWindowMinutes : 60;

    /// <summary>Effective minimum sample size for the failure-rate condition (never non-positive).</summary>
    public int EffectiveDeliveryFailureMinAttempts =>
        DeliveryFailureMinAttempts > 0 ? DeliveryFailureMinAttempts : 10;

    /// <summary>Effective failure-rate trip percentage (always within 1–100).</summary>
    public int EffectiveDeliveryFailurePercent =>
        DeliveryFailurePercent is > 0 and <= 100 ? DeliveryFailurePercent : 50;

    /// <summary>Effective pull-channel staleness threshold (never non-positive).</summary>
    public int EffectivePullChannelStaleMinutes =>
        PullChannelStaleMinutes > 0 ? PullChannelStaleMinutes : 60;
}
