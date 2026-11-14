using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>
/// Turns a provider bounce or spam complaint into a dead-lettered flow delivery and a failed document.
/// Matches the notification's key against <see cref="FlowDelivery.DispatchKey"/>, which flow sends stamp as a "N"-format GUID.
/// </summary>
public sealed class FlowDeliveryBounceHandler(ProcuLinkDbContext db, IBackgroundJobClient jobs, ILogger<FlowDeliveryBounceHandler> logger) : IFlowDeliveryBounceHandler
{
    public const string HardBounceError = "The recipient's email server refused this email. Check the address on this destination.";
    public const string SpamComplaintError = "The recipient marked this email as spam. Check with them before sending to this address again.";

    public async Task<DeliveryBounceResult?> TryHandleAsync(DeliveryBounceNotification notification, CancellationToken ct)
    {
        if (!Guid.TryParseExact(notification.IdempotencyKey, "N", out var dispatchKey)) return null;

        // The key discovers the tenant, so this lookup is org-blind; every later query is scoped by the row's OrgId.
        var found = await db.Set<FlowDelivery>().AsNoTracking().Where(d => d.DispatchKey == dispatchKey)
            .Select(d => new { d.Id, d.OrgId, d.DocumentId }).FirstOrDefaultAsync(ct);
        if (found is null) return null;

        // Same order as DeliverDocumentJob: the document row lock first, then the delivery re-read.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await db.FlowDocuments.Where(d => d.Id == found.DocumentId && d.OrgId == found.OrgId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, now), ct);
        var delivery = await db.Set<FlowDelivery>().FirstAsync(d => d.Id == found.Id && d.OrgId == found.OrgId, ct);
        if (delivery.Status != FlowDeliveryStatus.Delivered) return new DeliveryBounceResult(DeliveryBounceOutcome.AlreadyResolved);

        var doc = await db.FlowDocuments.FirstAsync(d => d.Id == found.DocumentId && d.OrgId == found.OrgId, ct);
        delivery.Status = FlowDeliveryStatus.DeadLetter;
        delivery.LastError = notification.Kind == DeliveryBounceKind.SpamComplaint ? SpamComplaintError : HardBounceError;
        delivery.UpdatedAt = now;
        var before = await DeliverDocumentJob.RollUpAsync(db, doc, delivery, now, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await DeliverDocumentJob.AnnounceAsync(db, jobs, doc, before, logger, CancellationToken.None);
        DeliverDocumentJob.LogDeadLetter(logger, delivery, "bounced");
        return new DeliveryBounceResult(DeliveryBounceOutcome.FlowDeliveryMarkedFailed);
    }
}
