namespace ProcuLink.Core.Services;

/// <summary>
/// Tunables for the recurring data-retention sweep. Bound from configuration section
/// <c>DataRetention</c> where present; otherwise the safe defaults below apply.
/// <para>
/// SAFE BY DEFAULT: <see cref="Enabled"/> is <c>false</c>, so an unconfigured deploy never
/// deletes anything. Windows default to conservative values (180 days for append-only history,
/// 48 hours for idempotency keys). A configured window of zero or negative is treated as
/// "use the default" — it can never collapse to "delete everything".
/// </para>
/// </summary>
public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    /// <summary>Master on/off. Default <c>false</c> — the sweep is a no-op until explicitly enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Retention window for <c>audit_events</c>. Default 180 days.</summary>
    public int AuditEventDays { get; set; } = 180;

    /// <summary>Retention window for <c>po_passport_events</c>. Default 180 days.</summary>
    public int PassportEventDays { get; set; } = 180;

    /// <summary>Retention window for <c>idempotency_keys</c>, in HOURS. Default 48 hours.</summary>
    public int IdempotencyKeyHours { get; set; } = 48;

    /// <summary>Retention window for <c>delivery_attempts</c> (terminal orders only). Default 180 days.</summary>
    public int DeliveryAttemptDays { get; set; } = 180;

    /// <summary>
    /// Retention window for <c>order_exceptions</c> in state <c>resolved</c> or <c>ignored</c>.
    /// Exceptions in state <c>open</c> are NEVER pruned. Default 180 days.
    /// </summary>
    public int OrderExceptionDays { get; set; } = 180;

    /// <summary>
    /// Retention window for <c>purchase_orders.inbound_sender_domain</c>. Default 365 days
    /// (founder ruling D2, 2026-07-25). Unlike every other window here this SCRUBS a column rather
    /// than deleting a row — the order is business data and stays; only the captured sender domain
    /// ages out.
    /// </summary>
    public int InboundSenderDomainDays { get; set; } = 365;

    /// <summary>
    /// Max rows deleted per table per sweep run. Keeps each delete bounded so a large backlog
    /// is drained over several runs rather than in one unbounded statement. Default 5,000.
    /// </summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>
    /// When a single sweep run deletes more than this many total rows, a structured warning is
    /// emitted so operators are alerted to unexpected growth. Default 1,000.
    /// </summary>
    public int HighVolumeAlertThreshold { get; set; } = 1_000;

    private static int PositiveOr(int value, int fallback) => value > 0 ? value : fallback;

    /// <summary>Effective audit-event window (never non-positive).</summary>
    public TimeSpan AuditEventWindow => TimeSpan.FromDays(PositiveOr(AuditEventDays, 180));

    /// <summary>Effective passport-event window (never non-positive).</summary>
    public TimeSpan PassportEventWindow => TimeSpan.FromDays(PositiveOr(PassportEventDays, 180));

    /// <summary>Effective idempotency-key window (never non-positive).</summary>
    public TimeSpan IdempotencyKeyWindow => TimeSpan.FromHours(PositiveOr(IdempotencyKeyHours, 48));

    /// <summary>Effective delivery-attempt window (never non-positive).</summary>
    public TimeSpan DeliveryAttemptWindow => TimeSpan.FromDays(PositiveOr(DeliveryAttemptDays, 180));

    /// <summary>Effective order-exception window (never non-positive).</summary>
    public TimeSpan OrderExceptionWindow => TimeSpan.FromDays(PositiveOr(OrderExceptionDays, 180));

    /// <summary>Effective inbound-sender-domain window (never non-positive).</summary>
    public TimeSpan InboundSenderDomainWindow => TimeSpan.FromDays(PositiveOr(InboundSenderDomainDays, 365));

    /// <summary>Effective per-table batch cap (never non-positive).</summary>
    public int EffectiveBatchSize => PositiveOr(BatchSize, 5_000);

    /// <summary>Effective high-volume alert threshold (never non-positive).</summary>
    public int EffectiveHighVolumeAlertThreshold => PositiveOr(HighVolumeAlertThreshold, 1_000);
}
