using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire recurring job (every 5 minutes, all orgs): re-enqueues deliveries a lost job left behind (an unscoped Worker context, so no org filter applies), and repairs a
/// document stuck in <c>sending</c> with nothing in flight. <see cref="DeliverDocumentJob"/> is idempotent, so a re-enqueue is safe.
/// </summary>
public class DocumentDeliverySweepJob(ProcuLinkDbContext db, IBackgroundJobClient jobs)
{
    public static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(10);

    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StrandedAfter;
        var stranded = await db.Set<FlowDelivery>()
            .Where(d => (d.Status == FlowDeliveryStatus.Queued && d.UpdatedAt < cutoff)
                     || (d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < cutoff)
                     || (d.Status == FlowDeliveryStatus.Failed && d.NextAttemptAt < cutoff))
            .Select(d => new { d.Id, d.OrgId }).ToListAsync(ct);
        foreach (var d in stranded) DeliverDocumentJob.Enqueue(jobs, d.Id, d.OrgId);

        // A crash between the sender's claim and its save leaves a document in "sending" that nothing will ever move on.
        var stuck = await db.FlowDocuments
            .Where(d => d.Status == DocumentStatus.Sending && d.UpdatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var doc in stuck)
        {
            var rows = await db.Set<FlowDelivery>()
                .Where(d => d.OrgId == doc.OrgId && d.DocumentId == doc.Id)
                .Select(d => new { d.OutputId, d.Status }).ToListAsync(ct);
            var counted = rows.Where(r => FlowDeliveryStatus.CountsTowardDocument(r.OutputId, r.Status)).Select(r => r.Status).ToList();
            if (counted.Any(s => s is FlowDeliveryStatus.Queued or FlowDeliveryStatus.Delivering or FlowDeliveryStatus.Failed)) continue;
            doc.Status = FlowDeliveryStatus.DocumentStatusFor(counted);
            doc.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
