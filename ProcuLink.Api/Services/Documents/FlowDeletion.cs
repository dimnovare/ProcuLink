using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>A flow that was never put in use and never sent anything may be deleted, with its documents.</summary>
public static class FlowDeletion
{
    public static async Task<bool> CanDeleteAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct) =>
        flow.PublishedVersionNo is null && !flow.IsInbox
        && !await db.Set<FlowDelivery>().AnyAsync(d => d.OrgId == flow.OrgId
            && db.FlowDocuments.Any(doc => doc.OrgId == flow.OrgId && doc.Id == d.DocumentId && doc.FlowId == flow.Id), ct);

    /// <summary>Null when deleted, else <c>flow_in_use</c> or <c>flow_busy</c>. Destinations and lookups are the workspace's and stay.</summary>
    public static async Task<string?> DeleteAsync(ProcuLinkDbContext db, IDocumentEraser eraser, Flow flow, string userId, CancellationToken ct)
    {
        if (!await CanDeleteAsync(db, flow, ct)) return "flow_in_use";
        var orgId = flow.OrgId;
        // The eraser takes the stored files with the rows; a cascade from the flow would leave them behind.
        var docIds = await db.FlowDocuments.Where(d => d.OrgId == orgId && d.FlowId == flow.Id).Select(d => d.Id).ToListAsync(ct);
        if (docIds.Count > 0)
        {
            var receipt = await eraser.EraseAsync(orgId, new DocumentEraseFilter(Ids: docIds), $"user:{userId}", ct);
            if (receipt.SkippedInFlight.Count > 0 || receipt.More) return "flow_busy";
        }
        // Read before the cascade removes the intakes: their ledger rows have no foreign key.
        var intakeIds = await db.FlowIntakes.Where(i => i.OrgId == orgId && i.FlowId == flow.Id).Select(i => i.Id).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Flows.Where(f => f.OrgId == orgId && f.Id == flow.Id && !f.IsInbox && f.PublishedVersionNo == null
                && !db.FlowDocuments.Any(d => d.OrgId == orgId && d.FlowId == f.Id))
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            return await db.Flows.AnyAsync(f => f.OrgId == orgId && f.Id == flow.Id && f.PublishedVersionNo == null, ct)
                ? "flow_busy" : "flow_in_use";
        await db.FlowIntakeItems.Where(i => i.OrgId == orgId && (i.ScopeId == flow.Id || intakeIds.Contains(i.ScopeId))).ExecuteDeleteAsync(ct);
        await db.OrgInboundAddresses.Where(a => a.OrganisationId == orgId && a.FlowId == flow.Id).ExecuteDeleteAsync(ct);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = orgId, EntityType = "Flow", EntityId = flow.Id, Action = "flow.deleted",
            Payload = JsonSerializer.SerializeToDocument(new { flow = flow.Name, by = userId }), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }
}
