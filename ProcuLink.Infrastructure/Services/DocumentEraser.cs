using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IDocumentEraser"/>, in the shape of <see cref="OrganisationEraser"/>: rows go in one
/// transaction with the receipt, files after the commit, best-effort. Deliveries and read calls go by their
/// <c>ON DELETE CASCADE</c> to flow_documents, counted first so the receipt still names them.
/// </summary>
public sealed class DocumentEraser(
    ProcuLinkDbContext db, IFileStorageService storage, ILogger<DocumentEraser> logger) : IDocumentEraser
{
    public const string AuditAction = "admin.documents_erased";
    public const int MaxPerCall = 500;

    public async Task<DocumentEraseReceipt> EraseAsync(Guid orgId, DocumentEraseFilter filter, string actor, CancellationToken ct)
    {
        if (db.ScopedOrganisationId is { } scoped && scoped != orgId)
            throw new InvalidOperationException($"DocumentEraser needs an unscoped DbContext; this one is scoped to {scoped}.");

        DocumentEraseReceipt Receipt(int erased = 0, List<Guid>? skipped = null, bool more = false,
            Dictionary<string, int>? rows = null, int blobs = 0, List<string>? failed = null) =>
            new(orgId, filter, erased, skipped ?? [], more, rows ?? [], blobs, failed ?? [], actor, DateTime.UtcNow);

        // An empty filter matches nothing: never "every document of the workspace".
        var ids = filter.Ids?.ToList();
        if (ids is { Count: 0 } || (ids is null && filter.ReceivedBefore is null)) return Receipt();
        DateTime? before = filter.ReceivedBefore is { } b
            ? b.Kind == DateTimeKind.Local ? b.ToUniversalTime() : DateTime.SpecifyKind(b, DateTimeKind.Utc)
            : null;

        var deliveries = db.Set<FlowDelivery>().Where(x => x.OrgId == orgId);
        var matches = db.FlowDocuments.Where(d => d.OrgId == orgId);
        if (ids is not null) matches = matches.Where(d => ids.Contains(d.Id));
        if (before is { } cutoff) matches = matches.Where(d => d.CreatedAt < cutoff);
        var sending = deliveries.Where(x => x.Status == FlowDeliveryStatus.Queued || x.Status == FlowDeliveryStatus.Delivering);

        // Sending: the sender sets it and uploads output files before it saves the delivery rows.
        var skipped = await matches.Where(d => d.Status == DocumentStatus.Reading || d.Status == DocumentStatus.Sending
                || sending.Any(x => x.DocumentId == d.Id))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Select(d => d.Id).ToListAsync(ct);
        var docs = await matches.Where(d => d.Status != DocumentStatus.Reading && d.Status != DocumentStatus.Sending
                && !sending.Any(x => x.DocumentId == d.Id))
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Take(MaxPerCall + 1)
            .Select(d => new { d.Id, d.SourceFileKey, d.ReadAt, d.IsSample, d.BillingUnits }).ToListAsync(ct);
        var more = docs.Count > MaxPerCall;
        if (more) docs.RemoveAt(MaxPerCall);
        if (docs.Count == 0) return Receipt(skipped: skipped, more: more);

        var docIds = docs.Select(d => d.Id).ToList();
        var linked = docIds.Select(id => (Guid?)id).ToList();
        var batchDeliveries = deliveries.Where(x => docIds.Contains(x.DocumentId));
        var knownKeys = docs.Select(d => d.SourceFileKey)
            .Concat(await batchDeliveries.Where(x => x.ArtifactKey != null).Select(x => x.ArtifactKey!).ToListAsync(ct))
            .Where(k => k != "").Distinct().ToList();
        var prefixes = docIds.Select(id => $"{orgId}/documents/{id}/").ToList();

        // The last point a cancellation may stop this: from here every call runs to completion.
        ct.ThrowIfCancellationRequested();
        var none = CancellationToken.None;
        await using var tx = await db.Database.BeginTransactionAsync(none);

        // Billed units outlive the document, dated by when it was READ so the month it was billed in keeps them.
        db.ErasedDocumentUnits.AddRange(docs.Where(d => d.ReadAt != null && !d.IsSample && d.BillingUnits > 0)
            .Select(d => new ErasedDocumentUnit { Id = Guid.NewGuid(), OrgId = orgId, ReadAt = d.ReadAt!.Value, BillingUnits = d.BillingUnits }));
        var rows = new Dictionary<string, int>
        {
            ["audit_events"] = await db.AuditEvents.Where(e => e.OrgId == orgId && docIds.Contains(e.EntityId)).ExecuteDeleteAsync(none),
            // Refused, never null: a null marker is taken again after IntakeLedger.RetakeAfter and the item re-read.
            ["flow_intake_items_unlinked"] = await db.FlowIntakeItems.Where(i => i.OrgId == orgId && linked.Contains(i.DocumentId))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DocumentId, (Guid?)IntakeLedger.Refused), none),
            ["flow_deliveries"] = await batchDeliveries.CountAsync(none),
            ["flow_read_calls"] = await db.Set<FlowReadCall>().CountAsync(c => c.OrgId == orgId && docIds.Contains(c.DocumentId), none),
        };
        rows["flow_documents"] = await db.FlowDocuments.Where(d => d.OrgId == orgId && docIds.Contains(d.Id)).ExecuteDeleteAsync(none);

        // Until cleanup confirms them, every key and folder is listed as failed, so a crash after the commit
        // still leaves an honest record of what may remain in storage.
        var audit = new PlatformAuditEvent
        {
            Id = Guid.NewGuid(), Action = AuditAction, SubjectOrgId = orgId, Actor = actor, CreatedAt = DateTime.UtcNow,
        };
        void Record(DocumentEraseReceipt r) => audit.Payload = JsonSerializer.Serialize(r, OrganisationEraser.ReceiptJson);
        Record(Receipt(rows["flow_documents"], skipped, more, rows, 0, [.. knownKeys, .. prefixes]));
        db.PlatformAuditEvents.Add(audit);
        await db.SaveChangesAsync(none);
        await tx.CommitAsync(none);

        var failed = new List<string>();
        var keys = new HashSet<string>(knownKeys);
        foreach (var prefix in prefixes)
        {
            try { keys.UnionWith(await storage.ListKeysAsync(prefix, none)); }
            catch (Exception ex)
            {
                failed.Add(prefix);
                logger.LogError(ex, "Erase documents of {OrgId}: could not list {Prefix}.", orgId, prefix);
            }
        }
        var blobs = 0;
        foreach (var key in keys)
        {
            try { await storage.DeleteAsync(key, none); blobs++; }
            catch (Exception ex)
            {
                failed.Add(key);
                logger.LogError(ex, "Erase documents of {OrgId}: failed to delete blob {Key}.", orgId, key);
            }
        }
        var receipt = Receipt(rows["flow_documents"], skipped, more, rows, blobs, failed);
        Record(receipt);
        await db.SaveChangesAsync(none);

        logger.LogWarning("DOCUMENT ERASE: {Count} documents of {OrgId} erased by {Actor} — {Blobs} blobs, {Failed} failed, {Skipped} skipped, more {More}.",
            receipt.DocumentsErased, orgId, actor, blobs, failed.Count, skipped.Count, more);
        return receipt;
    }
}
