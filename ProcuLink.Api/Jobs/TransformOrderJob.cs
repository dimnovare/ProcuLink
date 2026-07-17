using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: transforms a resolved order to the requested output format
/// and uploads the artifact to storage. Idempotent via the atomic ready/transforming
/// claim inside <c>OrderTransformService.TransformAsync</c>: a duplicated or retried
/// job on an already-transformed order gets a <see cref="TransformResponse.Skipped"/>
/// response and must NOT re-enqueue delivery (that would double-send the PO).
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
    [Queue("critical")]
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

        if (result.Value!.Skipped)
        {
            // The transform claim matched 0 rows: the order is already transformed, or a concurrent
            // transform is in flight. USUALLY the run that produced the artifact also enqueued its
            // delivery — re-enqueuing then would be redundant (though DeliverOrderJob's atomic claim
            // makes it harmless). But there is ONE case where delivery was NEVER enqueued: the first
            // run crashed AFTER committing ready_to_deliver + the artifact and BEFORE this method's
            // DeliverOrderJob.Enqueue below. This inline path recovers that strand on the very next
            // Hangfire retry; StrandedReadyOrderDetectionService is the systemic backstop that catches
            // it (and any other path leaving such a strand) within its aged window regardless.
            await TryRecoverStrandedDeliveryAsync(
                orderId, organisationId, result.Value.ArtifactId, ct);
            return;
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

    // ── B1 lost-order recovery ────────────────────────────────────────────────

    /// <summary>
    /// Re-enqueues delivery for an order stranded in <c>ready_to_deliver</c> by a crash between the
    /// transform commit and the delivery enqueue. Acts ONLY on the exact stranded signature — the
    /// order is still <c>ready_to_deliver</c>, the reported artifact exists, and NO delivery attempt
    /// has been made against THAT artifact — so a concurrent in-flight transform, an
    /// already-delivering/-delivered order, or an order whose current artifact was already dispatched
    /// is never re-driven. Attempts predating the current artifact are deliberately ignored: they were
    /// made against a superseded payload (or sent nothing at all), so they are not evidence about this
    /// one. This is idempotent and cannot double-send: <see cref="DeliverOrderJob"/>'s own atomic
    /// <c>delivering</c> claim (+ per-order distributed mutex) is the authority — a duplicate or racing
    /// enqueue simply no-ops there.
    /// </summary>
    private async Task TryRecoverStrandedDeliveryAsync(
        Guid orderId, Guid organisationId, Guid artifactId, CancellationToken ct)
    {
        if (artifactId == Guid.Empty)
        {
            // No artifact was ever produced — there is nothing to deliver.
            _logger.LogInformation(
                "TransformOrderJob skipped for order {OrderId}: no artifact — not enqueueing delivery.",
                orderId);
            return;
        }

        var status = await _db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == organisationId)
            .Select(o => o.Status)
            .FirstOrDefaultAsync(ct);

        if (status != OrderStatusConstants.ReadyToDeliver)
        {
            // Delivery already happened / is happening, or the transform is genuinely still in
            // flight elsewhere — do NOT re-drive.
            _logger.LogInformation(
                "TransformOrderJob skipped for order {OrderId} (status={Status}): not a stranded ready_to_deliver — not enqueueing delivery.",
                orderId, status);
            return;
        }

        // An attempt row is not evidence that THIS artifact was dispatched — only an attempt made
        // against the current artifact is. (Rationale + the DEPENDS-ON that keeps the comparison
        // meaningful: StrandedReadyOrderDetectionService, which applies the same discriminator.) The
        // Skipped path's artifactId is the order's newest artifact — OrderTransformService's claimed==0
        // branch selects it OrderByDescending(CreatedAt) — so its CreatedAt is the cutoff.
        var artifactCreatedAt = await _db.OutboundArtifacts
            .Where(a => a.Id == artifactId && a.OrderId == orderId && a.OrgId == organisationId)
            .Select(a => (DateTime?)a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (artifactCreatedAt is null)
        {
            // The reported artifact does not exist for this order — nothing provable to deliver.
            _logger.LogInformation(
                "TransformOrderJob skipped for order {OrderId}: artifact {ArtifactId} not found — not enqueueing delivery.",
                orderId, artifactId);
            return;
        }

        var attemptedThisArtifact = await _db.DeliveryAttempts
            .AnyAsync(a => a.OrderId == orderId && a.OrgId == organisationId
                        && a.AttemptedAt >= artifactCreatedAt.Value, ct);

        if (attemptedThisArtifact)
        {
            // A dispatch for the current artifact already ran (or is in flight) → re-enqueueing could
            // double-send this payload.
            _logger.LogInformation(
                "TransformOrderJob skipped for order {OrderId}: the current artifact was already attempted — not re-enqueueing.",
                orderId);
            return;
        }

        _logger.LogWarning(
            "TransformOrderJob recovering STRANDED order {OrderId}: ready_to_deliver with artifact {ArtifactId} and no delivery attempt (crash between transform commit and delivery enqueue) — re-enqueueing delivery.",
            orderId, artifactId);

        DeliverOrderJob.Enqueue(_jobs, orderId, organisationId, artifactId);
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
