using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: dispatches a transformed outbound artifact through
/// the supplier delivery workflow. The workflow owns delivery state transitions.
/// </summary>
public class DeliverOrderJob
{
    private readonly IDeliveryService _deliveryService;
    private readonly ILogger<DeliverOrderJob> _logger;

    public DeliverOrderJob(
        IDeliveryService deliveryService,
        ILogger<DeliverOrderJob> logger)
    {
        _deliveryService = deliveryService;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task ExecuteAsync(
        Guid orderId,
        Guid organisationId,
        Guid artifactId,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "DeliverOrderJob starting for order {OrderId}, artifact {ArtifactId}",
            orderId,
            artifactId);

        var result = await _deliveryService.DispatchArtifactAsync(
            organisationId,
            orderId,
            artifactId,
            requireAutoDeliver: true,
            ct);

        if (!result.Success)
        {
            _logger.LogWarning(
                "DeliverOrderJob finished with delivery failure for order {OrderId}: {Error}",
                orderId,
                result.ErrorMessage);
        }
    }

    public static void Enqueue(
        IBackgroundJobClient jobs,
        Guid orderId,
        Guid organisationId,
        Guid artifactId)
    {
        jobs.Enqueue<DeliverOrderJob>(j =>
            j.ExecuteAsync(orderId, organisationId, artifactId, CancellationToken.None));
    }
}
