using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services.Documents;

public static class FlowSummaries
{
    /// <summary>The order the intake kinds are declared in <see cref="IntakeKind"/>.</summary>
    private static readonly string[] KindOrder = [IntakeKind.Email, IntakeKind.Api, .. IntakeKind.Pickups];

    /// <summary>A summary for each of <paramref name="flows"/>, read with one org-wide query per table rather than one per flow.
    /// Outputs are the live version's once a flow is published, else the editable ones.</summary>
    public static async Task<IReadOnlyDictionary<Guid, FlowSummaryDto>> ForOrgAsync(
        ProcuLinkDbContext db, Guid orgId, IReadOnlyCollection<Flow> flows, CancellationToken ct)
    {
        var docs = await db.FlowDocuments.AsNoTracking().Where(d => d.OrgId == orgId).GroupBy(d => d.FlowId)
            .Select(g => new
            {
                FlowId = g.Key,
                NeedsYou = g.Count(d => !d.IsSample && (d.Status == DocumentStatus.NeedsReview || d.Status == DocumentStatus.Ready)),
                CouldNotSend = g.Count(d => d.Status == DocumentStatus.SendFailed),
                CouldNotRead = g.Count(d => d.Status == DocumentStatus.ReadFailed),
                Last = g.Max(d => (DateTime?)d.CreatedAt),
            })
            .ToDictionaryAsync(x => x.FlowId, ct);
        var waysIn = (await db.Set<FlowIntake>().AsNoTracking().Where(i => i.OrgId == orgId && i.IsEnabled)
                .Select(i => new { i.FlowId, i.Kind }).Distinct().ToListAsync(ct))
            .ToLookup(i => i.FlowId, i => i.Kind);
        var outputs = await OutputsInUseAsync(db, orgId, flows, ct);

        return flows.ToDictionary(f => f.Id, f =>
        {
            var d = docs.GetValueOrDefault(f.Id);
            var kinds = waysIn[f.Id].OrderBy(k => Array.IndexOf(KindOrder, k) is var i and >= 0 ? i : int.MaxValue)
                .ThenBy(k => k, StringComparer.Ordinal).ToList();
            return new FlowSummaryDto(d?.NeedsYou ?? 0, d?.CouldNotSend ?? 0, d?.CouldNotRead ?? 0, d?.Last, kinds, outputs[f.Id].ToList());
        });
    }

    /// <summary>The files each of <paramref name="flows"/> makes: the live version's once published, else the editable ones,
    /// each with its destination's name (null for download-only, or a destination that is gone).</summary>
    public static async Task<ILookup<Guid, FlowSummaryOutputDto>> OutputsInUseAsync(
        ProcuLinkDbContext db, Guid orgId, IReadOnlyCollection<Flow> flows, CancellationToken ct)
    {
        var flowIds = flows.Select(f => f.Id).ToList();
        var editable = (await db.Set<FlowOutput>().AsNoTracking().Where(o => o.OrgId == orgId && flowIds.Contains(o.FlowId))
                .Select(o => new { o.FlowId, o.Id, o.Name, o.Format, o.Standard, o.DestinationId }).ToListAsync(ct))
            .ToLookup(o => o.FlowId, o => (o.Id, o.Name, o.Format, o.Standard, o.DestinationId));
        var live = (await FlowVersions.LiveVersionOutputsByFlowAsync(db, orgId, ct, flowIds))
            .ToLookup(x => x.FlowId, x => (x.Output.Id, x.Output.Name, x.Output.Format, x.Output.Standard, x.Output.DestinationId));
        // Only the destinations these outputs name.
        var destinationIds = editable.SelectMany(g => g).Concat(live.SelectMany(g => g)).Select(o => o.DestinationId).OfType<Guid>().Distinct().ToList();
        var destinations = destinationIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Set<FlowDestination>().AsNoTracking().Where(d => d.OrgId == orgId && destinationIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return flows.SelectMany(f => (f.PublishedVersionNo is null ? editable[f.Id] : live[f.Id])
                .OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id)
                .Select(o => (f.Id, Output: new FlowSummaryOutputDto(o.Name, o.Format, o.Standard,
                    o.DestinationId is { } id ? destinations.GetValueOrDefault(id) : null))))
            .ToLookup(x => x.Id, x => x.Output);
    }

    /// <summary>True when no file the flow makes has a live destination: none at all, or every destination deleted (the sender then stores).</summary>
    public static bool DownloadOnly(IEnumerable<FlowSummaryOutputDto> outputs) => outputs.All(o => o.Destination is null);
}
