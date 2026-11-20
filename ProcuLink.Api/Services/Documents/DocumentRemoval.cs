using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Services.Documents;

/// <summary>
/// A person removes one document: the eraser takes it whole, exactly as deleting its flow would (row, deliveries, read calls,
/// its own audit rows, the original and the made files). A read document stays counted in the month it was read, because the
/// eraser keeps its units. Refused while the document is being received, read or sent.
/// </summary>
public static class DocumentRemoval
{
    public const string Action = "document.removed";

    /// <summary>Null when removed (or already gone), else <c>document_busy</c>.</summary>
    public static async Task<string?> RemoveAsync(ProcuLinkDbContext db, IDocumentEraser eraser, ILogger logger,
        FlowDocument doc, string flowName, string userId, CancellationToken ct)
    {
        if (doc.Status is DocumentStatus.Received or DocumentStatus.Reading or DocumentStatus.Sending
            || await db.FlowDocuments.Where(d => d.OrgId == doc.OrgId && d.Id == doc.Id).AnyAsync(DocumentEraser.InFlight(db, doc.OrgId), ct))
            return "document_busy";
        try
        {
            var receipt = await eraser.EraseAsync(doc.OrgId, new DocumentEraseFilter(Ids: [doc.Id]), $"user:{userId}", ct);
            if (receipt.SkippedInFlight.Count > 0) return "document_busy";
        }
        catch (DocumentsChangedDuringEraseException) { return "document_busy"; }

        // History, best effort: no file name, since a removed document leaves no contents behind.
        try
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(), OrgId = doc.OrgId, EntityType = "Flow", EntityId = doc.FlowId, Action = Action,
                Payload = JsonSerializer.SerializeToDocument(new { flow = flowName, by = userId }), CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removed document {DocumentId} but could not record it in the flow's history", doc.Id);
        }
        return null;
    }
}
