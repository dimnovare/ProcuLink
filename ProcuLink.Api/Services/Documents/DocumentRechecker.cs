using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>
/// The one place that recomputes a document's checks and status. Does not save.
/// A document is a duplicate of an EARLIER one, or of any copy the flow already sent or is sending: an original does turn into a
/// duplicate of its later copy once that copy was sent, so the number is never sent twice.
/// </summary>
public sealed class DocumentRechecker(ProcuLinkDbContext db, DocumentEngineGate gate, IStandardValidator validator)
{
    /// <summary>Rechecks the flow's documents waiting for review against its current config. Does not save.</summary>
    public async Task RecheckPendingAsync(Flow flow, CancellationToken ct)
    {
        var pending = await db.FlowDocuments
            // A file held as a duplicate stays exactly as the hold wrote it; only "Read it anyway" (a fresh read) rechecks it.
            .Where(d => d.OrgId == flow.OrgId && d.FlowId == flow.Id && d.Status == DocumentStatus.NeedsReview && d.ReadAt != null && d.StatusReason != "duplicate_file")
            .ToListAsync(ct);
        var config = await FlowConfigLoader.CurrentAsync(db, flow, ct);
        var batch = new OutputRuleChecks.Batch();
        foreach (var doc in pending)
            await ApplyAsync(doc, flow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), ct, config, batch);
    }

    public async Task ApplyAsync(FlowDocument doc, Flow flow, ReadResult read, CancellationToken ct, FlowConfig? config = null, OutputRuleChecks.Batch? batch = null)
    {
        // Spec section 6: the document number, else the file hash.
        doc.DedupKey = DocumentChecks.DedupKeyOf(read) ?? (string.IsNullOrEmpty(doc.Sha256) ? null : DocumentChecks.FileHashKeyPrefix + doc.Sha256);

        var duplicate = false;
        if (doc.DedupKey is { } key)
        {
            var keyHash = RemovedDocumentFingerprint.HashOf(key);
            // A sample is invented: its number never makes a real document a repeat.
            var others = await db.FlowDocuments
                .Where(d => d.OrgId == doc.OrgId && d.FlowId == doc.FlowId && d.DedupKey == key && d.Id != doc.Id && !d.IsSample)
                .Select(d => new { d.Id, d.CreatedAt, d.Status })
                .ToListAsync(ct);
            // A copy the flow already sent, or is sending, is a repeat whenever it arrived: a document moved in later must not send the number twice.
            duplicate = others.Any(d => d.Status is DocumentStatus.Sent or DocumentStatus.Sending || d.CreatedAt < doc.CreatedAt
                || (d.CreatedAt == doc.CreatedAt && d.Id.CompareTo(doc.Id) < 0))
                // A sent copy a person removed is an earlier copy.
                || await db.RemovedDocumentFingerprints.AnyAsync(f => f.OrgId == doc.OrgId && f.FlowId == doc.FlowId && f.DedupKeyHash == keyHash, ct);
        }

        // Anyone who knows a workspace's address can email it: on a flow that sends by itself, the first emailed document from an address
        // the workspace never sent a document from waits for a person. Once a person sends it, the address is trusted.
        var newSender = FlowAutomation.SendsByItself(flow.Automation) && !doc.IsSample && doc.Source is IntakeKind.Email or IntakeKind.Imap
            && (doc.SourceRef?.Trim().ToLowerInvariant() is not { Length: > 0 } sender
                || !await db.FlowDocuments.AnyAsync(d => d.OrgId == doc.OrgId && d.Id != doc.Id && !d.IsSample
                    && (d.Source == IntakeKind.Email || d.Source == IntakeKind.Imap) && d.Status == DocumentStatus.Sent && d.SourceRef != null && d.SourceRef.Trim().ToLower() == sender, ct));

        config ??= await FlowConfigLoader.CurrentAsync(db, flow, ct);
        doc.FlowVersion = config.VersionNo;
        var failed = DocumentChecks.Run(read, config.Fields, config.FieldsConfirmed, duplicate, newSender);
        // Rule checks are a heads-up, never a block: they join the list after the status is decided.
        doc.Status = DocumentChecks.StatusFor(failed);
        IReadOnlyList<DocumentCheck> withRules = [.. failed, .. await OutputRuleChecks.RunAsync(gate, validator, doc.OrgId, read, config, ct, batch)];
        doc.ChecksJson = DocumentJson.Serialize(withRules);
        // A sort that was not certain waits for a person, whatever the checks say. (No caller rechecks a held duplicate file.)
        if (doc.SortUnconfirmed)
        {
            doc.Status = DocumentStatus.NeedsReview;
            doc.StatusReason = DocumentSort.CheckSort;
        }
        doc.UpdatedAt = DateTime.UtcNow;
    }
}
