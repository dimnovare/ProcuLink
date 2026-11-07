using System.Linq.Expressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: delivers one queued flow delivery through its destination's dispatcher. Claim-first, so a duplicate
/// activation finds zero rows and does nothing. It schedules its own retries and dead-letters at the attempt cap.
/// A crash or a thrown cancellation leaves the row <c>delivering</c> until <see cref="ReclaimAfter"/> passes; then the next
/// activation (or the sweep) reclaims it, and dead-letters it instead of re-sending when the channel cannot de-duplicate.
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
        // A fresh claim (queued or failed) differs from a reclaim (a delivering row whose owner went quiet): a reclaim's outcome is unknown.
        var claimed = await ClaimAsync(deliveryId, organisationId, d => d.Status == FlowDeliveryStatus.Queued || (d.Status == FlowDeliveryStatus.Failed && (d.NextAttemptAt == null || d.NextAttemptAt <= now)), now, ct);
        var reclaimed = false;
        if (claimed == 0)
        {
            claimed = await ClaimAsync(deliveryId, organisationId, d => d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < stale, now, ct);
            reclaimed = true;
        }
        if (claimed == 0) return;

        var delivery = await db.Set<FlowDelivery>().FirstAsync(d => d.Id == deliveryId && d.OrgId == organisationId, ct);
        var doc = await db.FlowDocuments.FirstAsync(d => d.Id == delivery.DocumentId && d.OrgId == organisationId, ct);
        var destination = delivery.DestinationId is { } destinationId
            ? await db.Set<FlowDestination>().FirstOrDefaultAsync(d => d.Id == destinationId && d.OrgId == organisationId, ct)
            : null;
        var dispatcher = destination is null
            ? null
            : dispatchers.FirstOrDefault(x => string.Equals(x.Protocol, destination.Protocol, StringComparison.OrdinalIgnoreCase));
        var flowName = await db.Flows.Where(f => f.Id == doc.FlowId && f.OrgId == organisationId).Select(f => f.Name).FirstAsync(ct);
        var config = destination is null ? null : FlowDestinationService.ToDeliveryConfig(destination, flowName, test: false);

        string? deadLetter = null;
        var credentials = "";
        if (destination is null) deadLetter = "The destination was removed.";
        else if (dispatcher is null) deadLetter = "No dispatcher for this destination.";
        else if (reclaimed && (dispatcher.ResendSafety == ResendSafety.Unsafe || DeliveryService.CannotRepairItsOwnFile(config!)))
            deadLetter = "This may already have been sent. Check with the recipient before sending again.";
        else
            try { credentials = destinations.DecryptCredentials(destination); }
            catch (CredentialUnbindableException) { deadLetter = "Destination credentials could not be decrypted."; }

        DeliveryResult result;
        if (deadLetter is not null)
            result = new DeliveryResult(false, deadLetter, Outcome: DeliveryOutcome.NotRetryable);
        else
            try
            {
                using var buffer = new MemoryStream();
                await using (var artifact = await storage.DownloadAsync(delivery.ArtifactKey!, ct)) await artifact.CopyToAsync(buffer, ct);
                result = await dispatcher!.DispatchAsync(
                    buffer.ToArray(), delivery.FileName, delivery.ContentType, config!, credentials, ct,
                    idempotencyKey: delivery.DispatchKey.ToString("N"), isTestFire: false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Dispatchers return failures rather than throw; a storage or SDK fault (even a timeout) is treated the same way.
                logger.LogError(ex, "DeliverDocumentJob could not dispatch delivery {DeliveryId}", deliveryId);
                result = new DeliveryResult(false, "The output could not be sent.");
            }

        if (destination is not null) FlowDestinationService.RecordLearnedHostKey(destination, result);

        // Sibling deliveries finish concurrently: take the document row's lock first so each roll-up sees the others' committed result.
        // The dispatch is done and its result known: a shutdown must not roll that back.
        var save = CancellationToken.None;
        await using var tx = await db.Database.BeginTransactionAsync(save);
        var finishedAt = DateTime.UtcNow;
        await db.FlowDocuments.Where(d => d.Id == doc.Id && d.OrgId == organisationId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, finishedAt), save);

        delivery.UpdatedAt = finishedAt;
        delivery.ResponseCode = result.ResponseCode;
        delivery.NextAttemptAt = null;
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

        var siblings = await db.Set<FlowDelivery>().Where(d => d.OrgId == organisationId && d.DocumentId == doc.Id && d.Id != delivery.Id).ToListAsync(save);
        doc.Status = FlowDeliveryStatus.DocumentStatusFor(
            siblings.Append(delivery).Where(d => FlowDeliveryStatus.CountsTowardDocument(d.OutputId, d.Status)).Select(d => d.Status));
        doc.UpdatedAt = finishedAt;
        if (retryIn is null)
            DocumentAudit.Add(db, doc, result.Success ? "document.delivered" : "document.delivery_failed",
                new { output = delivery.OutputName, protocol = delivery.Protocol, responseCode = delivery.ResponseCode, error = delivery.LastError }, null);
        await db.SaveChangesAsync(save);
        await tx.CommitAsync(save);

        if (retryIn is { } delay) Schedule(jobs, deliveryId, organisationId, delay);
        else if (doc.Status is DocumentStatus.Sent or DocumentStatus.SendFailed)
            await FlowWebhook.EnqueueAsync(db, jobs, doc, doc.Status == DocumentStatus.Sent ? FlowWebhookEvent.Sent : FlowWebhookEvent.SendFailed, logger, save);
    }

    private Task<int> ClaimAsync(Guid deliveryId, Guid orgId, Expression<Func<FlowDelivery, bool>> eligible, DateTime now, CancellationToken ct) =>
        db.Set<FlowDelivery>().Where(d => d.Id == deliveryId && d.OrgId == orgId).Where(eligible)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, FlowDeliveryStatus.Delivering)
                .SetProperty(d => d.ClaimedAt, now)
                .SetProperty(d => d.Attempts, d => d.Attempts + 1)
                .SetProperty(d => d.UpdatedAt, now), ct);

    public static void Enqueue(IBackgroundJobClient jobs, Guid deliveryId, Guid organisationId) =>
        jobs.Enqueue<DeliverDocumentJob>(j => j.ExecuteAsync(deliveryId, organisationId, CancellationToken.None));

    public static void Schedule(IBackgroundJobClient jobs, Guid deliveryId, Guid organisationId, TimeSpan delay) =>
        jobs.Schedule<DeliverDocumentJob>(j => j.ExecuteAsync(deliveryId, organisationId, CancellationToken.None), delay);
}
