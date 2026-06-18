using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire server filter that serialises job runs PER ORDER by holding a storage-backed
/// distributed lock keyed on the job's order-id argument for the duration of
/// <see cref="OnPerformed"/>. The free <c>[DisableConcurrentExecution]</c> would key on the
/// method only — serialising EVERY order's retry through one global lock — and per-argument
/// mutexing otherwise needs the paid <c>Hangfire.Pro</c> <c>[Mutex]</c>. This filter gives the
/// per-order mutex on the OSS <c>Hangfire.PostgreSql</c> storage already in use.
///
/// <para>
/// It is the OUTER guard for <see cref="RetryDeliveryJob"/>: two activations for the SAME order
/// (a duplicated job racing the operator "Retry now" button, or a backoff-scheduled run firing
/// alongside a manual one) cannot run the body concurrently and double-dispatch. The atomic
/// status claim inside <c>DeliveryService.RetryDeliveryAsync</c> is the INNER guard — correct on
/// its own even across processes — so the two are defence-in-depth, not redundant: this filter
/// keeps the bodies from interleaving in the first place.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerOrderDistributedMutexAttribute : JobFilterAttribute, IServerFilter
{
    /// <summary>Zero-based position of the order-id <see cref="Guid"/> in the job method's arguments.</summary>
    private readonly int _orderArgumentIndex;

    /// <summary>
    /// How long to wait to acquire the per-order lock before giving up. A waiting activation that
    /// times out throws (Hangfire treats the job as failed and retries it later) rather than
    /// running the body concurrently with the holder.
    /// </summary>
    private readonly TimeSpan _timeout;

    private const string LockHandleKey = "PerOrderDistributedMutex:Lock";

    public PerOrderDistributedMutexAttribute(int orderArgumentIndex = 0, int timeoutSeconds = 60)
    {
        _orderArgumentIndex = orderArgumentIndex;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public void OnPerforming(PerformingContext context)
    {
        var resource = BuildResource(context.BackgroundJob.Job);
        // AcquireDistributedLock blocks up to _timeout, then throws — a waiting activation never
        // runs the body alongside the holder. The handle is stashed for release in OnPerformed.
        var handle = context.Connection.AcquireDistributedLock(resource, _timeout);
        context.Items[LockHandleKey] = handle;
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(LockHandleKey, out var handle) && handle is IDisposable disposable)
            disposable.Dispose();
    }

    private string BuildResource(Job job)
    {
        // Defensive: if the argument shape is ever not the expected (Guid orderId, ...) the filter
        // falls back to a method-level lock rather than throwing — degrading to coarser
        // serialisation, never crashing the job. The inner status claim still guarantees
        // correctness regardless.
        if (job.Args is { Count: > 0 } args
            && _orderArgumentIndex < args.Count
            && args[_orderArgumentIndex] is Guid orderId)
        {
            return $"retry-delivery:order:{orderId:N}";
        }

        return $"retry-delivery:{job.Type.Name}.{job.Method.Name}";
    }
}
