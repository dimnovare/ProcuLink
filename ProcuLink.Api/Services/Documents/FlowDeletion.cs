using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Services.Documents;

/// <summary>A flow that was never put in use and never sent anything may be deleted, with its documents.</summary>
public static class FlowDeletion
{
    public static async Task<bool> CanDeleteAsync(ProcuLinkDbContext db, Flow flow, CancellationToken ct) =>
        flow.PublishedVersionNo is null && !flow.IsInbox
        && !await db.Set<FlowDelivery>().AnyAsync(d => d.OrgId == flow.OrgId
            && db.FlowDocuments.Any(doc => doc.OrgId == flow.OrgId && doc.Id == d.DocumentId && doc.FlowId == flow.Id), ct)
        // A sent document a person removed still means the flow sent: its fingerprint must not go with the flow.
        && !await db.RemovedDocumentFingerprints.AnyAsync(f => f.OrgId == flow.OrgId && f.FlowId == flow.Id, ct);

    /// <summary>Null when deleted (or already gone), else <c>flow_in_use</c> or <c>flow_busy</c>. Destinations and lookups are the workspace's and stay.</summary>
    public static async Task<string?> DeleteAsync(ProcuLinkDbContext db, IDocumentEraser eraser, Flow flow, string userId, CancellationToken ct)
    {
        if (!await CanDeleteAsync(db, flow, ct)) return "flow_in_use";
        var orgId = flow.OrgId;
        var docs = db.FlowDocuments.Where(d => d.OrgId == orgId && d.FlowId == flow.Id);
        // Refused before anything is erased: a document being sent may leave, and then the flow must stay whole.
        if (await docs.AnyAsync(DocumentEraser.InFlight(db, orgId), ct)) return "flow_busy";
        // The eraser takes the stored files with the rows; a cascade from the flow would leave them behind.
        var docIds = await docs.Select(d => d.Id).ToListAsync(ct);
        for (var round = 0; docIds.Count > 0; round++)
        {
            if (round == MaxEraseRounds) return "flow_busy";
            DocumentEraseReceipt receipt;
            try { receipt = await eraser.EraseAsync(orgId, new DocumentEraseFilter(Ids: docIds), $"user:{userId}", ct); }
            catch (DocumentsChangedDuringEraseException) { return "flow_busy"; }
            if (receipt.SkippedInFlight.Count > 0) return "flow_busy";
            if (!receipt.More) break;
        }
        // Read before the cascade removes the intakes: their ledger rows have no foreign key.
        var intakeIds = await db.FlowIntakes.Where(i => i.OrgId == orgId && i.FlowId == flow.Id).Select(i => i.Id).ToListAsync(ct);

        // Serializable: a document committed into the flow meanwhile fails the delete instead of going by the cascade.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var deleted = await db.Flows.Where(f => f.OrgId == orgId && f.Id == flow.Id && !f.IsInbox && f.PublishedVersionNo == null
                    && !db.FlowDocuments.Any(d => d.OrgId == orgId && d.FlowId == f.Id))
                .ExecuteDeleteAsync(ct);
            if (deleted == 0)
            {
                var left = await db.Flows.Where(f => f.OrgId == orgId && f.Id == flow.Id).Select(f => new { f.PublishedVersionNo }).FirstOrDefaultAsync(ct);
                return left is null ? null : left.PublishedVersionNo is null ? "flow_busy" : "flow_in_use";
            }
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
        catch (Exception ex) when ((ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState
                                   is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation)
        {
            db.ChangeTracker.Clear();
            return "flow_busy";
        }
    }

    private const int MaxEraseRounds = 50;
}