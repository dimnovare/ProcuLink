using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services.Alerting;

/// <summary>
/// Counts dead-lettered and stuck flow deliveries across all organisations. The stuck predicate uses the
/// same clocks as the document-delivery sweep, at three times its stranded age.
/// </summary>
public sealed class FlowDeliveryHealthProbe(ProcuLinkDbContext db, WorkerHealthAlertOptions options) : IFlowDeliveryHealthProbe
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    public async Task<FlowDeliveryHealthSignal> GetSignalAsync(CancellationToken ct)
    {
        db.UseCrossOrganisationScope(
            "flow delivery health probe: counts dead-lettered and stuck flow deliveries across ALL " +
            "organisations as a system-fault signal for operator alerting, not a tenant-facing number.");

        var now = DateTime.UtcNow;
        var windowStart = now - options.PipelineFailureWindow;
        var cutoff = now - StuckAfter;
        var rows = db.Set<FlowDelivery>();

        var dead = await rows.CountAsync(d => d.Status == FlowDeliveryStatus.DeadLetter && d.UpdatedAt >= windowStart, ct);
        var stuck = await rows.CountAsync(d =>
            (d.Status == FlowDeliveryStatus.Queued && d.UpdatedAt < cutoff)
            || (d.Status == FlowDeliveryStatus.Delivering && d.ClaimedAt < cutoff)
            || (d.Status == FlowDeliveryStatus.Failed && d.NextAttemptAt < cutoff), ct);

        return new FlowDeliveryHealthSignal(dead, options.EffectivePipelineFailureWindowMinutes, stuck, (int)StuckAfter.TotalMinutes);
    }
}
