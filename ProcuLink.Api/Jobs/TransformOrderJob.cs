using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: transforms a resolved order to the requested output format
/// and uploads the artifact to storage.  Idempotent via status check in OrderService.
/// </summary>
public class TransformOrderJob
{
    private readonly IOrderService           _orderService;
    private readonly IBackgroundJobClient    _jobs;
    private readonly ILogger<TransformOrderJob> _logger;
    private readonly ProcuLinkDbContext      _db;
    private readonly IAnalyticsService       _analytics;

    public TransformOrderJob(
        IOrderService orderService,
        IBackgroundJobClient jobs,
        ILogger<TransformOrderJob> logger,
        ProcuLinkDbContext db,
        IAnalyticsService analytics)
    {
        _orderService = orderService;
        _jobs         = jobs;
        _logger       = logger;
        _db           = db;
        _analytics    = analytics;
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

        // Analytics: emit "first_transform_succeeded" the FIRST time an order
        // successfully transforms for an organisation. Org-scoped query; excludes
        // the current order so its own newly-set post-transform status doesn't
        // suppress the emission. Idempotent on retry: once any other org order
        // reaches a post-transform status, the event will not fire again.
        var hadOtherTransformedOrders = await _db.PurchaseOrders
            .AnyAsync(o => o.OrgId == organisationId
                        && o.Id != orderId
                        && (o.Status == OrderStatusConstants.ReadyToDeliver
                            || o.Status == OrderStatusConstants.Delivering
                            || o.Status == OrderStatusConstants.Delivered
                            || o.Status == OrderStatusConstants.DeliveryFailed), ct);

        if (!hadOtherTransformedOrders)
        {
            await _analytics.CaptureAsync(
                organisationId: organisationId,
                userId: null,
                eventName: "first_transform_succeeded",
                properties: new Dictionary<string, object?>
                {
                    ["order_id"] = orderId,
                    ["output_format"] = format,
                },
                ct: ct);
        }

        DeliverOrderJob.Enqueue(_jobs, orderId, organisationId, result.Value.ArtifactId);
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
