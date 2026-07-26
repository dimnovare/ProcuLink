namespace ProcuLink.Core.Services;

/// <summary>
/// Recurring cross-tenant maintenance sweep that prunes append-only / ephemeral tables
/// past their retention window so they do not grow unbounded:
/// <list type="bullet">
///   <item><description><c>audit_events</c> older than the audit window</description></item>
///   <item><description><c>po_passport_events</c> older than the passport window</description></item>
///   <item><description><c>idempotency_keys</c> older than the idempotency window (hours)</description></item>
///   <item><description><c>delivery_attempts</c> older than the delivery window — but ONLY for
///   orders in a terminal state (or test-fire rows with no order), to never drop the
///   audit trail of a still-active delivery</description></item>
/// </list>
/// Deletes strictly by a created/occurred timestamp older than each window, in bounded
/// batches, and is idempotent: a second run with nothing past the window deletes nothing.
/// Disabled by default; enable + tune via the <c>DataRetention</c> configuration section.
/// </summary>
public interface IDataRetentionService
{
    /// <summary>
    /// Runs one retention sweep across all configured tables. Returns the per-table and total
    /// row counts deleted. A no-op (all zero) when retention is disabled or nothing is past
    /// any window.
    /// </summary>
    Task<DataRetentionResult> RunAsync(CancellationToken ct);
}

/// <summary>Row counts deleted by one <see cref="IDataRetentionService.RunAsync"/> sweep.</summary>
public sealed record DataRetentionResult(
    int AuditEvents,
    int PassportEvents,
    int IdempotencyKeys,
    int DeliveryAttempts,
    int OrderExceptions = 0,
    int InboundSenderDomainsScrubbed = 0)
{
    public static readonly DataRetentionResult Empty = new(0, 0, 0, 0);

    /// <summary>
    /// Total rows AFFECTED across every table the sweep touched. Includes the sender-domain
    /// scrub, which is an update rather than a delete — it is still a row the retention policy
    /// acted on, and leaving it out of the total would under-report what the sweep did.
    /// </summary>
    public int Total =>
        AuditEvents + PassportEvents + IdempotencyKeys + DeliveryAttempts + OrderExceptions
        + InboundSenderDomainsScrubbed;
}
