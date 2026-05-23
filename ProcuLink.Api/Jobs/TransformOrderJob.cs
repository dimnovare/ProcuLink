using Hangfire;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: transforms a resolved order to the requested output format
/// and uploads the artifact to storage.  Idempotent via status check in OrderService.
/// </summary>
public class TransformOrderJob
{
    private readonly IOrderService           _orderService;
    private readonly ILogger<TransformOrderJob> _logger;

    public TransformOrderJob(IOrderService orderService, ILogger<TransformOrderJob> logger)
    {
        _orderService = orderService;
        _logger       = logger;
    }

    /// <summary>Entry point called by Hangfire.</summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 60, 300 })]
    public async Task ExecuteAsync(
        Guid orderId,
        Guid organisationId,
        string format,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "TransformOrderJob starting for order {OrderId}, format={Format}",
            orderId, format);

        if (!Enum.TryParse<OutputFormat>(format, ignoreCase: true, out var outputFormat))
        {
            _logger.LogError("Unknown output format '{Format}' for order {OrderId}", format, orderId);
            return; // non-retriable — bad input
        }

        var result = await _orderService.TransformAsync(organisationId, orderId, outputFormat, ct);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "TransformOrderJob failed for order {OrderId}: {Error}",
                orderId, result.Error);
            throw new InvalidOperationException($"Transform failed: {result.Error}");
        }

        _logger.LogInformation(
            "TransformOrderJob completed for order {OrderId}, artifactId={ArtifactId}",
            orderId, result.Value!.ArtifactId);
    }

    // ── Static factory method ─────────────────────────────────────────────────

    public static void Enqueue(
        IBackgroundJobClient jobs,
        Guid orderId,
        Guid organisationId,
        string format)
    {
        jobs.Enqueue<TransformOrderJob>(j =>
            j.ExecuteAsync(orderId, organisationId, format, CancellationToken.None));
    }
}
