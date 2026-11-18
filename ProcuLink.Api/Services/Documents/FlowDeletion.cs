using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>A flow that was never put in use and never sent anything may be deleted, with its documents.</summary>
public static class FlowDeletion
{
    public static async Task<bool> CanDeleteAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct) =>
        flow.PublishedVersionNo is null && !flow.IsInbox
        && !await db.Set<FlowDelivery>().AnyAsync(d => d.OrgId == flow.OrgId
            && db.FlowDocuments.Any(doc => doc.OrgId == flow.OrgId && doc.Id == d.DocumentId && doc.FlowId == flow.Id), ct);
}
