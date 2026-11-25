using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: sends a freshly read document when its flow sends by itself. On <c>auto</c> a failed check never blocks it, with
/// two exceptions: a repeated document number waits for a person, because on an unattended flow it would be a second posting; and
/// so does the first document from a new email address, because anyone who knows the workspace's address can email it. On
/// <c>marked</c> only a document with no failed check (status <c>ready</c>) is sent; anything marked waits for a person.
/// It loads the document itself so the sender's claim sees the row as it is now. Paused processing or any other refusal
/// leaves the document as it was, for a person to send.
/// </summary>
public class SendDocumentJob(ProcuLinkDbContext db, DocumentSender sender)
{
    [Queue("critical")]
    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(Guid documentId, Guid organisationId, CancellationToken ct)
    {
        var doc = await db.FlowDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null) return;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == doc.FlowId && f.OrgId == organisationId, ct);
        if (flow is null || !ShouldSend(doc, flow)) return;
        await sender.SendAsync(doc, SendMode.Automatic, null, ct);
    }

    // The sample, an unsure sort, a repeated number and a new sender wait for a person (DocumentSender.HeldForPerson). On marked,
    // ready means no check failed, so the last two never reach it there.
    private static bool ShouldSend(FlowDocument doc, Flow flow) =>
        DocumentSender.HeldForPerson(doc) is null && DocumentStatus.IsEditable(doc.Status, doc.StatusReason) && flow.Automation switch
        {
            FlowAutomation.Auto => doc.Status is DocumentStatus.NeedsReview or DocumentStatus.Ready,
            FlowAutomation.Marked => doc.Status == DocumentStatus.Ready,
            _ => false,
        };

    public static void EnqueueIfAutomatic(IBackgroundJobClient jobs, FlowDocument doc, Flow flow)
    {
        if (!ShouldSend(doc, flow)) return;
        var (id, org) = (doc.Id, doc.OrgId);
        jobs.Enqueue<SendDocumentJob>(j => j.ExecuteAsync(id, org, CancellationToken.None));
    }
}
