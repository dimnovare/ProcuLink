using Hangfire;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Concrete <see cref="IRetryDeliveryEnqueuer"/> that re-drives a stuck delivery through the
/// Hangfire-backed <see cref="RetryDeliveryJob"/>. Used by <c>StuckDeliveryDetectionService</c>
/// to recover orders stranded in <c>delivering</c>.
/// </summary>
/// <remarks>
/// Lives in <c>ProcuLink.Infrastructure.Jobs</c> alongside <see cref="RetryDeliveryJob"/> (both
/// already depend on Hangfire.Core + <see cref="IBackgroundJobClient"/>). The enqueued retry is
/// idempotent and attempt-capped inside <c>RetryDeliveryAsync</c>, so a duplicate enqueue is safe.
/// </remarks>
public sealed class HangfireRetryDeliveryEnqueuer : IRetryDeliveryEnqueuer
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireRetryDeliveryEnqueuer(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
    {
        RetryDeliveryJob.Enqueue(_jobs, orderId, orgId);
        return Task.CompletedTask;
    }
}
