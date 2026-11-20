using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
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
/// <para>Who: an org admin removes any document; a member only one that never had a delivery and is not sent (nothing was
/// made, sent or downloaded from it), so a member cannot wipe what was delivered. Flow deletion keeps the same line (<see cref="FlowDeletion.CanDeleteAsync"/>).</para>
/// </summary>
public static class DocumentRemoval
{
    public const string Action = "document.removed";
    public const string Busy = "document_busy";
    public const string AdminRequired = "org_admin_required";

    /// <summary>The one rule, for the endpoint and for every DTO's <c>canRemove</c>.
    /// A sent document counts as made or sent even with no delivery row: a download marks it sent and writes none.</summary>
    public static bool CanRemove(bool isAdmin, bool hasDelivery, string status) => isAdmin || (!hasDelivery && status != DocumentStatus.Sent);

    /// <summary>The rule for the current caller, for the DTOs only. The endpoint asks <see cref="RequireOrgAdminAttribute.IsAdmin"/> itself.</summary>
    public static bool CanRemove(HttpContext http, bool hasDelivery, string status) => CanRemove(RequireOrgAdminAttribute.IsAdmin(http), hasDelivery, status);

    public static Task<bool> HasDeliveryAsync(ProcuLinkDbContext db, FlowDocument doc, CancellationToken ct) =>
        db.Set<FlowDelivery>().AnyAsync(x => x.OrgId == doc.OrgId && x.DocumentId == doc.Id, ct);

    /// <summary>Null when removed (or already gone), else <see cref="AdminRequired"/> or <see cref="Busy"/>.</summary>
    public static async Task<string?> RemoveAsync(ProcuLinkDbContext db, IDocumentEraser eraser,
        FlowDocument doc, string flowName, string userId, bool isAdmin, CancellationToken ct)
    {
        if (!CanRemove(isAdmin, await HasDeliveryAsync(db, doc, ct), doc.Status)) return AdminRequired;
        if (doc.Status is DocumentStatus.Received or DocumentStatus.Reading or DocumentStatus.Sending
            || await db.FlowDocuments.Where(d => d.OrgId == doc.OrgId && d.Id == doc.Id).AnyAsync(DocumentEraser.InFlight(db, doc.OrgId), ct))
            return Busy;

        // History in the erase's own transaction: never one without the other. No file name, since a removed document leaves
        // no contents behind. For a member the eraser checks again that nothing was delivered, so a send that lands in between
        // keeps the document. A removed sent document leaves its fingerprint, so its next copy still waits as already received.
        var history = new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = doc.OrgId, EntityType = "Flow", EntityId = doc.FlowId, Action = Action,
            Payload = JsonSerializer.SerializeToDocument(new { flow = flowName, by = userId }), CreatedAt = DateTime.UtcNow,
        };
        DocumentEraseReceipt receipt;
        try { receipt = await eraser.EraseAsync(doc.OrgId, new DocumentEraseFilter(Ids: [doc.Id], NeverDelivered: !isAdmin, KeepFingerprints: true), $"user:{userId}", ct, history); }
        catch (DocumentsChangedDuringEraseException) { return Busy; }
        if (receipt.DocumentsErased == 1) return null;
        return await db.FlowDocuments.AnyAsync(d => d.OrgId == doc.OrgId && d.Id == doc.Id, ct) ? Busy : null;
    }
}
