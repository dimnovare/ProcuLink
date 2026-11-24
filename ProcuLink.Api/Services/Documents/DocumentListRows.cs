using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The app's rows for one page of documents. Extra queries: deliveries; the page's flows and the files they make (four);
/// destination names, only when a stop or a delivered file names one; earlier copies, live and removed (two), only when the page holds a repeated file.
/// None for an empty page.</summary>
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

    /// <param name="canRemove">The caller's removal rule, given whether the document has any delivery and its status (<see cref="DocumentRemoval.CanRemove(bool, bool, string)"/>).</param>
    /// <param name="usageSince">Where the plan's usage count starts (<see cref="StripeBillingService.UsageSince"/>).</param>
    public static async Task<List<DocumentRowDto>> ForAsync(ProcuLinkDbContext db, Guid orgId, IQueryable<FlowDocument> page, Func<bool, string, bool> canRemove,
        DateTime usageSince, CancellationToken ct)
    {
        var docs = await page.Select(d => new { Doc = d, FlowName = d.Flow!.Name, d.Flow.IsInbox }).ToListAsync(ct);
        if (docs.Count == 0) return [];

        var ids = docs.Select(r => r.Doc.Id).ToList();
        var deliveries = (await db.Set<FlowDelivery>().Where(x => x.OrgId == orgId && ids.Contains(x.DocumentId))
                .Select(x => new { x.DocumentId, x.Status, x.DeliveredAt, x.UpdatedAt, x.CreatedAt, x.LastError, x.DestinationId, x.OutputId, x.Id, HasFile = x.ArtifactKey != null, x.FileName, x.OutputName })
                .ToListAsync(ct))
            .ToLookup(x => x.DocumentId);

        // Where sending stopped: the newest dead-lettered delivery whose output still exists.
        var stops = docs.Where(r => r.Doc.Status == DocumentStatus.SendFailed)
            .Select(r => deliveries[r.Doc.Id].Where(x => x.Status == FlowDeliveryStatus.DeadLetter && x.OutputId != null).MaxBy(x => x.UpdatedAt))
            .Where(x => x is not null)
            .ToDictionary(x => x!.DocumentId, x => x!);
        // A sent document's files that can be downloaded: delivered or stored, with a made file.
        var files = docs.Where(r => r.Doc.Status == DocumentStatus.Sent)
            .ToDictionary(r => r.Doc.Id, r => deliveries[r.Doc.Id]
                .Where(x => x.HasFile && x.Status is FlowDeliveryStatus.Delivered or FlowDeliveryStatus.Stored).OrderBy(x => x.CreatedAt).ToList());
        var destinationIds = stops.Values.Select(x => x.DestinationId)
            .Concat(files.Values.SelectMany(f => f).Where(x => x.Status == FlowDeliveryStatus.Delivered).Select(x => x.DestinationId))
            .OfType<Guid>().Distinct().ToList();
        var names = destinationIds.Count == 0 ? []
            : await db.Set<FlowDestination>().Where(x => x.OrgId == orgId && destinationIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var flowIds = docs.Select(r => r.Doc.FlowId).Distinct().ToList();
        var flows = await db.Flows.AsNoTracking().Where(f => f.OrgId == orgId && flowIds.Contains(f.Id)).ToListAsync(ct);
        var outputs = await FlowSummaries.OutputsInUseAsync(db, orgId, flows, ct);

        // A repeated file: when the newest earlier copy of the same file in this workspace arrived.
        var repeats = docs.Where(r => r.Doc.StatusReason == "duplicate_file").Select(r => r.Doc).ToList();
        var shas = repeats.Select(d => d.Sha256).Distinct().ToList();
        var copies = (shas.Count == 0 ? []
            : (await db.FlowDocuments.Where(x => x.OrgId == orgId && shas.Contains(x.Sha256)).Select(x => new { x.Id, x.Sha256, x.CreatedAt }).ToListAsync(ct))
                .Select(x => (x.Id, x.Sha256, x.CreatedAt)).ToList())
            .ToLookup(x => x.Sha256);
        // The newest earlier copy, live or a sent one a person removed.
        var removed = (shas.Count == 0 ? []
            : await db.RemovedDocumentFingerprints.Where(x => x.OrgId == orgId && shas.Contains(x.Sha256)).Select(x => new { x.Sha256, x.DocumentCreatedAt }).ToListAsync(ct))
            .ToLookup(x => x.Sha256, x => x.DocumentCreatedAt);
        var duplicateOf = repeats.ToDictionary(d => d.Id,
            d => copies[d.Sha256].Where(x => x.Id != d.Id && x.CreatedAt < d.CreatedAt).Select(x => x.CreatedAt)
                .Concat(removed[d.Sha256].Where(at => at < d.CreatedAt)).Max(at => (DateTime?)at));
        var numbers = await EarlierNumbersAsync(db, orgId, docs.Select(r => r.Doc).ToList(), ct);

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
                Files = files.GetValueOrDefault(d.Id)?.Select(x => new DocumentFileDto(x.Id, x.FileName is { Length: > 0 } name ? name : x.OutputName,
                    x.Status == FlowDeliveryStatus.Delivered,
                    x.Status == FlowDeliveryStatus.Delivered && x.DestinationId is { } to ? names.GetValueOrDefault(to) : null)).ToList() ?? [],
                DuplicateOfAt = duplicateOf.GetValueOrDefault(d.Id),
                DuplicateOf = numbers.GetValueOrDefault(d.Id),
                DownloadOnly = FlowSummaries.DownloadOnly(outputs[d.FlowId]),
                CanRemove = canRemove(deliveries[d.Id].Any(), d.Status),
                CountsThisMonth = StripeBillingService.CountsAsUsage(d, usageSince),
            };
        }).ToList();
    }

    /// <summary>For each document whose number repeats (a failed <see cref="DocumentChecks.Duplicate"/> check), the copy that makes it one, by
    /// <see cref="DocumentRechecker"/>'s rule: the newest live copy, so the screen can open it, else a sent copy a person removed (no id).
    /// Two queries, none when no document repeats.</summary>
    public static async Task<Dictionary<Guid, DuplicateOfDto>> EarlierNumbersAsync(ProcuLinkDbContext db, Guid orgId, IReadOnlyCollection<FlowDocument> docs, CancellationToken ct)
    {
        var repeats = docs.Where(d => d.DedupKey != null
            && DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Any(c => c.Code == DocumentChecks.Duplicate && !c.Passed)).ToList();
        if (repeats.Count == 0) return [];
        var keys = repeats.Select(d => d.DedupKey!).Distinct().ToList();
        var hashes = keys.Select(RemovedDocumentFingerprint.HashOf).ToList();
        var flowIds = repeats.Select(d => d.FlowId).Distinct().ToList();
        var live = (await db.FlowDocuments.Where(x => x.OrgId == orgId && flowIds.Contains(x.FlowId) && keys.Contains(x.DedupKey!) && !x.IsSample)
                .Select(x => new { x.Id, x.FlowId, x.DedupKey, x.CreatedAt, x.Status }).ToListAsync(ct))
            .ToLookup(x => (x.FlowId, x.DedupKey));
        var removed = (await db.RemovedDocumentFingerprints.Where(f => f.OrgId == orgId && flowIds.Contains(f.FlowId) && hashes.Contains(f.DedupKeyHash!))
                .Select(f => new { f.FlowId, f.DedupKeyHash, f.DocumentCreatedAt }).ToListAsync(ct))
            .ToLookup(f => (f.FlowId, f.DedupKeyHash), f => f.DocumentCreatedAt);
        var result = new Dictionary<Guid, DuplicateOfDto>();
        foreach (var d in repeats)
        {
            var number = DocumentChecks.NumberOf(DocumentJson.Deserialize<ReadResult>(d.ReadJson));
            var copy = live[(d.FlowId, d.DedupKey)].Where(x => x.Id != d.Id && (x.Status is DocumentStatus.Sent or DocumentStatus.Sending
                || x.CreatedAt < d.CreatedAt || (x.CreatedAt == d.CreatedAt && x.Id.CompareTo(d.Id) < 0))).MaxBy(x => x.CreatedAt);
            if (copy is not null) result[d.Id] = new(copy.Id, copy.CreatedAt, number);
            else if (removed[(d.FlowId, RemovedDocumentFingerprint.HashOf(d.DedupKey!))].Max(at => (DateTime?)at) is { } at) result[d.Id] = new(null, at, number);
        }
        return result;
    }
}
