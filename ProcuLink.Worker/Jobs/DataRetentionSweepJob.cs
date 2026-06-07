using Hangfire;
using ProcuLink.Core.Services;

namespace ProcuLink.Worker.Jobs;

/// <summary>
/// Recurring Hangfire job (daily): thin wrapper over <see cref="IDataRetentionService"/> so the
/// prune logic stays unit-tested in the Infrastructure test suite (mirrors
/// <see cref="StuckOrderDetectionJob"/> / <see cref="DeliverySlaSweepJob"/>). Prunes append-only /
/// ephemeral tables (audit_events, po_passport_events, idempotency_keys, delivery_attempts) past
/// their retention window in bounded batches. Disabled by default (config-gated). Idempotent — a
/// second run with nothing past the window deletes nothing.
/// </summary>
public sealed class DataRetentionSweepJob
{
    private readonly IDataRetentionService _service;
    private readonly ILogger<DataRetentionSweepJob> _logger;

    public DataRetentionSweepJob(
        IDataRetentionService service,
        ILogger<DataRetentionSweepJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var result = await _service.RunAsync(ct);
        if (result.Total > 0)
            _logger.LogInformation(
                "DataRetentionSweepJob pruned {Total} row(s) — audit={Audit}, passport={Passport}, idempotency={Idempotency}, deliveryAttempts={Delivery}.",
                result.Total, result.AuditEvents, result.PassportEvents, result.IdempotencyKeys, result.DeliveryAttempts);
        else
            _logger.LogInformation("DataRetentionSweepJob run complete — nothing past any retention window.");
    }
}
