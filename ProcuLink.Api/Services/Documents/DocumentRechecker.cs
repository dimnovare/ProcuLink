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
