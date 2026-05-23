using Hangfire;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: parses the source file for a newly created order stub
/// and updates order lines + status.  Idempotent — safe to retry on transient failure.
/// </summary>
public class ParseOrderJob
{
    private readonly IOrderService        _orderService;
    private readonly ILogger<ParseOrderJob> _logger;

    public ParseOrderJob(IOrderService orderService, ILogger<ParseOrderJob> logger)
    {
        _orderService = orderService;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point called by Hangfire.
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid orderId, Guid organisationId, CancellationToken ct)
    {
        _logger.LogInformation("ParseOrderJob starting for order {OrderId}", orderId);

        var result = await _orderService.ParseStoredFileAsync(organisationId, orderId, ct);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "ParseOrderJob failed for order {OrderId}: {Error}",
                orderId, result.Error);

            // Throwing causes Hangfire to retry; once retries are exhausted it
            // moves to the failed queue. The service itself already set status="failed".
            throw new InvalidOperationException($"Parse failed: {result.Error}");
        }

        _logger.LogInformation(
            "ParseOrderJob completed for order {OrderId}, new status={Status}",
            orderId, result.Value!.Status);
    }

    // ── Static factory method for clean enqueue syntax ────────────────────────

    public static void Enqueue(IBackgroundJobClient jobs, Guid orderId, Guid organisationId)
    {
        jobs.Enqueue<ParseOrderJob>(j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None));
    }
}
