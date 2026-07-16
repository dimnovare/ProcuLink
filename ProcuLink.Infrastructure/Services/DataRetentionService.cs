using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// Cross-tenant maintenance sweep (mirrors <see cref="StuckOrderDetectionService"/> /
/// <see cref="DeliverySlaService"/>): it reads across all organisations, but every row it
/// considers is filtered strictly by that row's own created/occurred timestamp. Tenant
/// isolation is irrelevant for a pure age-based prune of already-tenant-scoped rows.
/// <para>
/// Each table is deleted via <see cref="DeleteOldestBatchAsync"/>, which performs a bounded
/// set-based <c>ExecuteDeleteAsync</c> (no entities materialised). That method is
/// <c>protected virtual</c> so unit tests — which run on the EF InMemory provider that does
/// not support <c>ExecuteDelete</c> — can override it with a tracked delete while still
/// exercising the real selection predicate (the load-bearing "only rows older than the
/// window" logic).
/// </para>
/// </remarks>
public class DataRetentionService : IDataRetentionService
{
    /// <summary>
    /// Orders whose delivery is finished. <c>delivery_attempts</c> for these may be pruned once
    /// past the window; attempts for orders in any other (still-active) state are KEPT so we
    /// never drop the audit trail of an in-flight delivery. Conservative by design.
    /// </summary>
    private static readonly string[] TerminalOrderStatuses =
    {
        OrderStatusConstants.Delivered,
        OrderStatusConstants.DeliveryDeadLetter,
        OrderStatusConstants.RejectedBySupplier,
        OrderStatusConstants.Failed,
        OrderStatusConstants.TransformFailed,
    };

    private readonly ProcuLinkDbContext _db;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionService> _logger;

    public DataRetentionService(
        ProcuLinkDbContext db,
        DataRetentionOptions options,
        ILogger<DataRetentionService> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public async Task<DataRetentionResult> RunAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("DataRetention: disabled (DataRetention:Enabled=false) — nothing pruned.");
            return DataRetentionResult.Empty;
        }

        var now   = DateTime.UtcNow;
        var batch = _options.EffectiveBatchSize;

        // Each cutoff is "now minus the window". We delete rows strictly OLDER than the cutoff
        // (timestamp < cutoff), so recent rows are never touched.
        var auditCutoff       = now - _options.AuditEventWindow;
        var passportCutoff    = now - _options.PassportEventWindow;
        var idempotencyCutoff = new DateTimeOffset(now - _options.IdempotencyKeyWindow, TimeSpan.Zero);
        var deliveryCutoff    = now - _options.DeliveryAttemptWindow;
        var exceptionCutoff   = now - _options.OrderExceptionWindow;

        var auditDeleted = await DeleteOldestBatchAsync(
            _db.AuditEvents.Where(e => e.CreatedAt < auditCutoff),
            batch, ct);

        var passportDeleted = await DeleteOldestBatchAsync(
            _db.PoPassportEvents.Where(e => e.OccurredAt < passportCutoff),
            batch, ct);

        var idempotencyDeleted = await DeleteOldestBatchAsync(
            _db.IdempotencyKeys.Where(k => k.CreatedAt < idempotencyCutoff),
            batch, ct);

        // delivery_attempts: prune only attempts past the window that are either test-fire rows
        // (no linked order) OR belong to an order in a terminal state. An attempt whose order is
        // still active is kept regardless of age.
        var terminalOrderIds = _db.PurchaseOrders
            .Where(o => TerminalOrderStatuses.Contains(o.Status))
            .Select(o => o.Id);

        var deliveryDeleted = await DeleteOldestBatchAsync(
            _db.DeliveryAttempts.Where(a =>
                a.AttemptedAt < deliveryCutoff
                && (a.OrderId == null || terminalOrderIds.Contains(a.OrderId.Value))),
            batch, ct);

        // order_exceptions: prune only resolved/ignored rows past the window.
        // NEVER prune 'open' exceptions — an operator may still need to action them.
        var exceptionsDeleted = await DeleteOldestBatchAsync(
            _db.OrderExceptions.Where(e =>
                e.CreatedAt < exceptionCutoff
                && (e.State == "resolved" || e.State == "ignored")),
            batch, ct);

        var result = new DataRetentionResult(
            AuditEvents:      auditDeleted,
            PassportEvents:   passportDeleted,
            IdempotencyKeys:  idempotencyDeleted,
            DeliveryAttempts: deliveryDeleted,
            OrderExceptions:  exceptionsDeleted);

        if (result.Total > 0)
            _logger.LogInformation(
                "DataRetention: pruned {Total} row(s) — audit_events={Audit}, po_passport_events={Passport}, idempotency_keys={Idempotency}, delivery_attempts={Delivery}, order_exceptions={Exceptions}.",
                result.Total, result.AuditEvents, result.PassportEvents, result.IdempotencyKeys, result.DeliveryAttempts, result.OrderExceptions);
        else
            _logger.LogInformation("DataRetention: run complete — nothing past any retention window.");

        // A2 — observability: warn when a single run deletes an unexpectedly large volume.
        // This lets operators spot table-growth surprises before they require partitioning.
        // Uses structured Warning (no Sentry dependency in this assembly) so it flows to
        // whatever log sink is wired (Railway logs, Datadog, etc.).
        var threshold = _options.EffectiveHighVolumeAlertThreshold;
        if (result.Total > threshold)
            _logger.LogWarning(
                "DataRetention: high-volume prune detected — {Total} row(s) deleted in one run (threshold={Threshold}). " +
                "audit_events={Audit}, po_passport_events={Passport}, idempotency_keys={Idempotency}, delivery_attempts={Delivery}, order_exceptions={Exceptions}. " +
                "Consider whether retention windows or batch size need tuning.",
                result.Total, threshold,
                result.AuditEvents, result.PassportEvents, result.IdempotencyKeys, result.DeliveryAttempts, result.OrderExceptions);

        return result;
    }

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> rows matched by <paramref name="query"/> using a
    /// single set-based <c>ExecuteDeleteAsync</c>. No ordering is applied — <c>ExecuteDelete</c>
    /// does not accept one — so WHICH expired rows a batch takes is undefined; that is fine
    /// because <paramref name="query"/> already narrows to rows past the retention window, so
    /// every candidate is equally deletable and the remainder is taken by the next run.
    /// Returns the number of rows deleted.
    /// <para>
    /// Overridable so InMemory-backed unit tests can substitute a tracked delete; production uses
    /// the real bulk delete and materialises nothing.
    /// </para>
    /// </summary>
    protected virtual Task<int> DeleteOldestBatchAsync<T>(
        IQueryable<T> query, int batchSize, CancellationToken ct)
        where T : class
        => query.Take(batchSize).ExecuteDeleteAsync(ct);
}
