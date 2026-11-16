using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The app's rows for one page of documents. Up to two extra queries (deliveries; destination names, only when a stop names a destination); none for an empty page.</summary>
public static class DocumentListRows
{
    /// <summary>Sent documents whose last output landed in [from, to). Both null: no filter.</summary>
    public static IQueryable<FlowDocument> SentBetween(ProcuLinkDbContext db, Guid orgId, IQueryable<FlowDocument> docs, DateTime? from, DateTime? to)
    {
        if (from is null && to is null) return docs;
        docs = docs.Where(d => d.Status == DocumentStatus.Sent);
        // The last output landed: the newest DeliveredAt among this document's own deliveries.
        if (from is { } a) docs = docs.Where(d => db.Set<FlowDelivery>().Where(x => x.OrgId == orgId && x.DocumentId == d.Id).Max(x => x.DeliveredAt) >= a);
        if (to is { } b) docs = docs.Where(d => db.Set<FlowDelivery>().Where(x => x.OrgId == orgId && x.DocumentId == d.Id).Max(x => x.DeliveredAt) < b);
        return docs;
    }

    public static async Task<List<DocumentRowDto>> ForAsync(ProcuLinkDbContext db, Guid orgId, IQueryable<FlowDocument> page, CancellationToken ct)
    {
        var docs = await page.Select(d => new { Doc = d, FlowName = d.Flow!.Name, d.Flow.IsInbox }).ToListAsync(ct);
        if (docs.Count == 0) return [];

        var ids = docs.Select(r => r.Doc.Id).ToList();
        var deliveries = (await db.Set<FlowDelivery>().Where(x => x.OrgId == orgId && ids.Contains(x.DocumentId))
                .Select(x => new { x.DocumentId, x.Status, x.DeliveredAt, x.UpdatedAt, x.LastError, x.DestinationId, x.OutputId })
                .ToListAsync(ct))
            .ToLookup(x => x.DocumentId);

        // Where sending stopped: the newest dead-lettered delivery whose output still exists.
        var stops = docs.Where(r => r.Doc.Status == DocumentStatus.SendFailed)
            .Select(r => deliveries[r.Doc.Id].Where(x => x.Status == FlowDeliveryStatus.DeadLetter && x.OutputId != null).MaxBy(x => x.UpdatedAt))
            .Where(x => x is not null)
            .ToDictionary(x => x!.DocumentId, x => x!);
        var destinationIds = stops.Values.Select(x => x.DestinationId).OfType<Guid>().Distinct().ToList();
        var names = destinationIds.Count == 0 ? []
            : await db.Set<FlowDestination>().Where(x => x.OrgId == orgId && destinationIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        return docs.Select(r =>
        {
            var d = r.Doc;
            var read = DocumentJson.Deserialize<ReadResult>(d.ReadJson);
            var stop = stops.GetValueOrDefault(d.Id);
            return new DocumentRowDto(DocumentSummaryDto.From(d, r.FlowName))
            {
                Number = DocumentChecks.NumberOf(read),
                Sender = SenderIdentity.Of(read, d.Source, d.SourceRef)?.Label,
                Checks = DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Select(c => c.Code).Distinct().ToList(),
                SentAt = d.Status == DocumentStatus.Sent ? deliveries[d.Id].Max(x => x.DeliveredAt) : null,
                FailedAt = d.Status switch
                {
                    DocumentStatus.ReadFailed => d.UpdatedAt,
                    DocumentStatus.SendFailed => stop?.UpdatedAt ?? d.UpdatedAt,
                    _ => null,
                },
                Stop = stop is null ? null
                    : new DocumentStopDto(stop.DestinationId, stop.DestinationId is { } id ? names.GetValueOrDefault(id) : null, stop.LastError),
                IsSample = d.IsSample,
                Kind = read.KindLabel?.Trim() is { Length: > 0 } kind ? kind[..Math.Min(kind.Length, 80)] : null,
                InInbox = r.IsInbox,
            };
        }).ToList();
    }
}
