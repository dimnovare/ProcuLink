using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Concrete <see cref="IDeliveryDispatchEnqueuer"/> that re-drives a stranded order through the
/// normal Hangfire-backed first-delivery job (<see cref="DeliverOrderJob"/>, <c>requireAutoDeliver:
/// true</c>). Used by <c>StrandedReadyOrderDetectionService</c> to recover orders left in
/// <c>ready_to_deliver</c> whose delivery enqueue was lost.
/// </summary>
/// <remarks>
/// Lives in <c>ProcuLink.Api.Jobs</c> alongside <see cref="DeliverOrderJob"/> (which
/// <c>ProcuLink.Infrastructure</c> can't reference), mirroring how <c>HangfireParseJobEnqueuer</c>
/// lives beside <c>ParseOrderJob</c>. The enqueued delivery is idempotent — <see cref="DeliverOrderJob"/>'s
/// atomic <c>delivering</c> claim (+ per-order mutex) prevents any double-send — so a duplicate
/// enqueue is safe.
/// </remarks>
public sealed class HangfireDeliveryDispatchEnqueuer : IDeliveryDispatchEnqueuer
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireDeliveryDispatchEnqueuer(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public Task EnqueueAsync(Guid orderId, Guid orgId, Guid artifactId, CancellationToken ct)
    {
        DeliverOrderJob.Enqueue(_jobs, orderId, orgId, artifactId);
        return Task.CompletedTask;
    }
}
