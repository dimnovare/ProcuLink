using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>
/// The one place that recomputes a document's checks and status. Does not save.
/// A document is a duplicate only of an EARLIER one, so an original never turns into a duplicate of its later copy.
/// </summary>
public sealed class DocumentRechecker(ProcuLinkDbContext db)
{
    /// <summary>Rechecks the flow's documents waiting for review against its current config. Does not save.</summary>
    public async Task RecheckPendingAsync(Flow flow, CancellationToken ct)
    {
        var pending = await db.FlowDocuments
            .Where(d => d.OrgId == flow.OrgId && d.FlowId == flow.Id && d.Status == DocumentStatus.NeedsReview && d.ReadAt != null)
            .ToListAsync(ct);
        var config = await FlowConfigLoader.CurrentAsync(db, flow, ct);
        foreach (var doc in pending)
            await ApplyAsync(doc, flow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), ct, config);
    }

    public async Task ApplyAsync(FlowDocument doc, Flow flow, ReadResult read, CancellationToken ct, FlowConfig? config = null)
    {
        // Spec section 6: the document number, else the file hash.
        doc.DedupKey = DocumentChecks.DedupKeyOf(read) ?? (string.IsNullOrEmpty(doc.Sha256) ? null : $"sha256:{doc.Sha256}");

        var duplicate = false;
        if (doc.DedupKey is { } key)
        {
            var others = await db.FlowDocuments
                .Where(d => d.OrgId == doc.OrgId && d.FlowId == doc.FlowId && d.DedupKey == key && d.Id != doc.Id)
                .Select(d => new { d.Id, d.CreatedAt })
                .ToListAsync(ct);
            duplicate = others.Any(d => d.CreatedAt < doc.CreatedAt
                || (d.CreatedAt == doc.CreatedAt && d.Id.CompareTo(doc.Id) < 0));
        }

        config ??= await FlowConfigLoader.CurrentAsync(db, flow, ct);
        doc.FlowVersion = config.VersionNo;
        var failed = DocumentChecks.Run(read, config.Fields, config.FieldsConfirmed, duplicate);
        doc.ChecksJson = DocumentJson.Serialize(failed);
        doc.Status = DocumentChecks.StatusFor(failed);
        doc.UpdatedAt = DateTime.UtcNow;
    }
}
