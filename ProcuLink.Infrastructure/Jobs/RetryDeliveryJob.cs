using Hangfire;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: operator-triggered delivery retry/replay with dead-letter
/// escalation. Delegates to <see cref="IDeliveryService.RetryDeliveryAsync"/>,
/// which is idempotent and org-scoped and owns the attempt-count / dead-letter
/// state transitions. After <see cref="MaxAttempts"/> failed delivery attempts the
/// order is moved to <c>delivery_dead_letter</c> and is no longer retried.
/// </summary>
public class RetryDeliveryJob
{
    /// <summary>Total delivery attempts allowed before dead-lettering.</summary>
    public const int MaxAttempts = 3;

    private readonly IDeliveryService _delivery;
    private readonly ILogger<RetryDeliveryJob> _logger;

    public RetryDeliveryJob(IDeliveryService delivery, ILogger<RetryDeliveryJob> logger)
    {
        _delivery = delivery;
        _logger = logger;
    }

    // No Hangfire AutomaticRetry: retry/backoff semantics live inside
    // RetryDeliveryAsync (attempt cap + dead-letter), not in Hangfire's queue.
    // A Hangfire-level retry would double-count attempts.
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid orderId, Guid organisationId, CancellationToken ct)
    {
        _logger.LogInformation(
            "RetryDeliveryJob starting for order {OrderId}, org {OrgId}", orderId, organisationId);

        var result = await _delivery.RetryDeliveryAsync(organisationId, orderId, MaxAttempts, ct);

        if (result.Success)
            _logger.LogInformation("RetryDeliveryJob delivered order {OrderId}", orderId);
        else
            _logger.LogWarning(
                "RetryDeliveryJob did not deliver order {OrderId}: {Error}", orderId, result.ErrorMessage);
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid orderId, Guid organisationId)
    {
        jobs.Enqueue<RetryDeliveryJob>(j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None));
    }
}
