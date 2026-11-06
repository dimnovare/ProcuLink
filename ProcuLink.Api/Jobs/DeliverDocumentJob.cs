using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: delivers one queued flow delivery through its destination's dispatcher. Claim-first, so a duplicate
/// activation finds zero rows and does nothing. It schedules its own retries and dead-letters at the attempt cap.
/// A crash or a thrown cancellation leaves the row <c>delivering</c> until <see cref="ReclaimAfter"/> passes; then the next
/// activation (or the sweep) reclaims it.
/// </summary>
public class DeliverDocumentJob(
    ProcuLinkDbContext db, IFileStorageService storage, FlowDestinationService destinations,
    IEnumerable<IDeliveryDispatcher> dispatchers, IBackgroundJobClient jobs, DeliveryReliabilityOptions reliability,
    ILogger<DeliverDocumentJob> logger)
{
    public static readonly TimeSpan ReclaimAfter = TimeSpan.FromMinutes(2);

    [Queue("critical")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid deliveryId, Guid organisationId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stale = now - ReclaimAfter;
        var claimed = await db.Set<FlowDelivery>()
            .Where(d => d.Id == deliveryId && d.OrgId == organisationId
                && (d.Status == FlowDeliveryStatus.Queued || d.Status == FlowDeliveryStatus.Failed
                    || (d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < stale)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, FlowDeliveryStatus.Delivering)
                .SetProperty(d => d.ClaimedAt, now)
                .SetProperty(d => d.Attempts, d => d.Attempts + 1)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (claimed == 0) return;

        var delivery = await db.Set<FlowDelivery>().FirstAsync(d => d.Id == deliveryId && d.OrgId == organisationId, ct);
        var doc = await db.FlowDocuments.FirstAsync(d => d.Id == delivery.DocumentId && d.OrgId == organisationId, ct);
        var destination = delivery.DestinationId is { } destinationId
            ? await db.Set<FlowDestination>().FirstOrDefaultAsync(d => d.Id == destinationId && d.OrgId == organisationId, ct)
            : null;
        var dispatcher = dispatchers.FirstOrDefault(x => string.Equals(x.Protocol, delivery.Protocol, StringComparison.OrdinalIgnoreCase));

        string? deadLetter = null;
        var credentials = "";
        if (destination is null) deadLetter = "The destination was removed.";
        else if (dispatcher is null) deadLetter = "No dispatcher for this destination.";
        else
            try { credentials = destinations.DecryptCredentials(destination); }
            catch (CredentialUnbindableException) { deadLetter = "Destination credentials could not be decrypted."; }

        DeliveryResult result;
        if (deadLetter is not null)
            result = new DeliveryResult(false, deadLetter, Outcome: DeliveryOutcome.NotRetryable);
        else
            try
            {
                var flowName = await db.Flows.Where(f => f.Id == doc.FlowId && f.OrgId == organisationId).Select(f => f.Name).FirstAsync(ct);
                using var buffer = new MemoryStream();
                await using (var artifact = await storage.DownloadAsync(delivery.ArtifactKey!, ct)) await artifact.CopyToAsync(buffer, ct);
                result = await dispatcher!.DispatchAsync(
                    buffer.ToArray(), delivery.FileName, delivery.ContentType,
                    FlowDestinationService.ToDeliveryConfig(destination!, flowName, test: false), credentials, ct,
                    idempotencyKey: delivery.Id.ToString("N"), isTestFire: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Dispatchers return failures rather than throw; a storage or config fault is treated the same way.
                logger.LogError(ex, "DeliverDocumentJob could not dispatch delivery {DeliveryId}", deliveryId);
                result = new DeliveryResult(false, "The output could not be sent.");
            }

        if (destination is not null) FlowDestinationService.RecordLearnedHostKey(destination, result);

        var finishedAt = DateTime.UtcNow;
        delivery.UpdatedAt = finishedAt;
        delivery.ResponseCode = result.ResponseCode;
        TimeSpan? retryIn = null;
        if (result.Success)
        {
            delivery.Status = FlowDeliveryStatus.Delivered;
            delivery.DeliveredAt = finishedAt;
            delivery.LastError = null;
        }
        else
        {
            delivery.LastError = result.ErrorMessage;
            if (result.Outcome == DeliveryOutcome.NotRetryable || delivery.Attempts >= reliability.MaxAttempts)
                delivery.Status = FlowDeliveryStatus.DeadLetter;
            else
            {
                retryIn = reliability.NextRetryDelay(delivery.Attempts, result.RetryAfter, Random.Shared.NextDouble());
                delivery.Status = FlowDeliveryStatus.Failed;
                delivery.NextAttemptAt = finishedAt + retryIn;
            }
        }

        var siblings = await db.Set<FlowDelivery>().Where(d => d.OrgId == organisationId && d.DocumentId == doc.Id && d.Id != delivery.Id).ToListAsync(ct);
        doc.Status = FlowDeliveryStatus.DocumentStatusFor(
            siblings.Append(delivery).Where(d => FlowDeliveryStatus.CountsTowardDocument(d.OutputId, d.Status)).Select(d => d.Status));
        doc.UpdatedAt = finishedAt;
        if (retryIn is null)
            DocumentAudit.Add(db, doc, result.Success ? "document.delivered" : "document.delivery_failed",
                new { output = delivery.OutputName, protocol = delivery.Protocol, responseCode = delivery.ResponseCode, error = delivery.LastError }, null);
        await db.SaveChangesAsync(ct);

        if (retryIn is { } delay) Schedule(jobs, deliveryId, organisationId, delay);
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid deliveryId, Guid organisationId) =>
        jobs.Enqueue<DeliverDocumentJob>(j => j.ExecuteAsync(deliveryId, organisationId, CancellationToken.None));

    public static void Schedule(IBackgroundJobClient jobs, Guid deliveryId, Guid organisationId, TimeSpan delay) =>
        jobs.Schedule<DeliverDocumentJob>(j => j.ExecuteAsync(deliveryId, organisationId, CancellationToken.None), delay);
}
