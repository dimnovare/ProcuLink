using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The workspace's hidden flow that documents land in when no flow is named.</summary>
public static class InboxFlow
{
    public const string Name = "New documents";

    /// <summary>This organisation's inbox flow, created on first use; a racing creator loses to the unique index and re-reads.</summary>
    public static async Task<Flow> GetOrCreateAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct)
    {
        if (await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.IsInbox, ct) is { } found) return found;
        var flow = FlowCreation.New(orgId, Name, FlowFieldList.Empty, confirmed: false);
        flow.IsInbox = true;
        db.Flows.Add(flow);
        try
        {
            await db.SaveChangesAsync(ct);
            return flow;
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            db.Entry(flow).State = EntityState.Detached;
            return await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.IsInbox, ct) ?? throw ex;
        }
    }

    /// <summary>The flows a person sees and counts: this organisation's, without the inbox.</summary>
    public static IQueryable<Flow> PersonFlows(this DbSet<Flow> flows, Guid orgId) =>
        flows.Where(f => f.OrgId == orgId && !f.IsInbox);
}
