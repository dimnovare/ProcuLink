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
    /// <para>
    /// <b>Deliberately NOT windowed</b>, unlike <see cref="PipelineFailureWindowMinutes"/>. The two
    /// conditions look alike and are not: this one counts statuses that HAVE a drain, and that one
    /// counted a status that has none. <c>OrderStatusMachine.RequeueableFrom</c> admits both
    /// <c>delivery_dead_letter</c> and <c>delivery_failed</c>, so the operations health page's
    /// requeue action moves an order out of this count — the number falls when the incident is
    /// handled. An undelivered purchase order is also a STANDING incident in a way an abandoned
    /// unparseable upload is not: it stays wrong, and worth re-raising, until somebody acts on it.
    /// A window here would stop paging about a supplier that is still not receiving orders.
    /// </para>
    /// </summary>
    public int DeadLetterThreshold { get; set; } = 1;

    /// <summary>
    /// All-org failed + transform-failed (pre-delivery pipeline) order count at/above which an
    /// alert fires. Default 1 — deliberately LOW, unlike <see cref="DeadLetterThreshold"/>: at
    /// pilot scale a single order stuck in <c>failed</c> or <c>transform_failed</c> means a broken
    /// parser or output template, and these orders reach no other alert condition. A non-positive
    /// configured value falls back to the default.
    /// <para>
    /// The count this is compared against is RECENT, not all-time — see
    /// <see cref="PipelineFailureWindowMinutes"/>. The threshold stays at 1 because the problem was
    /// never the height of the bar.
    /// </para>
    /// </summary>
    public int PipelineFailureThreshold { get; set; } = 1;

    /// <summary>
    /// Trailing window (minutes) over which the pipeline-failure count is taken, measured on each
    /// order's <c>UpdatedAt</c>. Default 1440 (24 h). A non-positive configured value falls back to
    /// the default.
    /// <para>
    /// <b>Why this window exists: the condition had no drain.</b> The count used to be an all-time
    /// count of orders whose CURRENT status is <c>failed</c> or <c>transform_failed</c>, and
    /// <c>failed</c> is declared terminal — <c>OrderStatusMachine.Transitions[Failed]</c> is the
    /// EMPTY set, so nothing can ever move an order out of it. Combined with a threshold of 1, one
    /// pilot user who uploaded a single unparseable file and walked away pinned the condition bad
    /// forever: it could never transition back to healthy, so it re-alerted every
    /// <see cref="MinAlertIntervalMinutes"/> for the life of the workspace — roughly 48 emails a
    /// day, permanently, about one abandoned file. An alert that can never clear trains its one
    /// reader to ignore the channel, which is the failure this whole file exists to prevent.
    /// </para>
    /// <para>
    /// A trailing window gives the condition the drain it lacked without needing a new column or a
    /// durable high-watermark: 24 hours after the last pipeline failure the count returns to zero,
    /// the condition goes healthy, and it is re-armed for the next genuine incident. It also states
    /// the true claim — a broken parser is a burst of RECENT failures, whereas a failure from three
    /// weeks ago is history, not an incident.
    /// </para>
    /// <para>
    /// <b>What the window costs.</b> An operator who ignores a real incident for 24 h stops being
    /// paged about it; the orders are still counted on the org-scoped operations health page, which
    /// is the surface that is supposed to hold a standing backlog. And because the clock is
    /// <c>UpdatedAt</c>, editing a long-failed order re-enters it into the window — acceptable,
    /// since a touched order is one somebody is working on.
    /// </para>
    /// </summary>
    public int PipelineFailureWindowMinutes { get; set; } = 1440;

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

    /// <summary>Effective pipeline-failure window in minutes (never non-positive).</summary>
    public int EffectivePipelineFailureWindowMinutes =>
        PipelineFailureWindowMinutes > 0 ? PipelineFailureWindowMinutes : 1440;

    /// <summary>Effective pipeline-failure window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PipelineFailureWindow =>
        TimeSpan.FromMinutes(EffectivePipelineFailureWindowMinutes);

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
