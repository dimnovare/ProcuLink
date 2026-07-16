using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire server filter that serialises job runs PER ORGANISATION by holding a storage-backed
/// distributed lock keyed on the job's orgId argument for the duration of <see cref="OnPerformed"/>.
///
/// <para>
/// It exists because the free <c>[DisableConcurrentExecution]</c> keys its lock on the job TYPE +
/// METHOD ONLY — never on the arguments — so on a per-org child job it degrades into ONE GLOBAL lock
/// per job type: every tenant's polling for a channel serialises through it, and one slow or hung
/// tenant endpoint blocks every other tenant for up to the lock timeout. Per-argument mutexing
/// otherwise needs the paid <c>Hangfire.Pro</c> <c>[Mutex]</c>. This filter delivers the per-org
/// mutex on the OSS <c>Hangfire.PostgreSql</c> storage already in use — the same mechanism
/// <see cref="PerOrderDistributedMutexAttribute"/> uses for the per-order case.
/// </para>
///
/// <para>
/// <b>Scope of the guarantee.</b> This is the OUTER guard for the pull-ingress poll children
/// (SFTP / S3 / IMAP): two activations for the SAME org cannot run the body concurrently and
/// double-import a file. It is defence-in-depth / TOCTOU-narrowing only — the claim-first ledger
/// insert plus its unique index, inside each ingress service, is what actually guarantees
/// exactly-one-order even across processes. Because correctness rests on the claim, per-ORG
/// granularity is sufficient: two DIFFERENT orgs have no shared state to race over, so making them
/// contend buys nothing and costs throughput.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PerOrgDistributedMutexAttribute : JobFilterAttribute, IServerFilter
{
    /// <summary>Zero-based position of the org-id <see cref="Guid"/> in the job method's arguments.</summary>
    private readonly int _orgArgumentIndex;

    /// <summary>
    /// How long to wait to acquire the per-org lock before giving up. A waiting activation that
    /// times out throws (Hangfire treats the job as failed and retries it later) rather than
    /// running the body concurrently with the holder.
    /// </summary>
    private readonly TimeSpan _timeout;

    private const string LockHandleKey = "PerOrgDistributedMutex:Lock";

    public PerOrgDistributedMutexAttribute(int orgArgumentIndex = 0, int timeoutSeconds = 300)
    {
        _orgArgumentIndex = orgArgumentIndex;
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

    /// <summary>
    /// The lock resource for a given job activation: <c>poll:{JobType}.{Method}:{orgId}</c>.
    ///
    /// <para>Keying on the orgId argument is the entire point — org A and org B take different locks
    /// and never contend. The job type + method are in the key too, so an org's SFTP poll doesn't
    /// serialise against its own S3 or IMAP poll (different endpoints, different ledgers).</para>
    ///
    /// <para>Public so the per-org keying can be unit-tested directly; the lock acquisition itself
    /// needs live storage and can't be.</para>
    /// </summary>
    public string BuildResource(Job job)
    {
        // Defensive: if the argument shape is ever not the expected (Guid orgId, ...) the filter
        // falls back to a method-level lock rather than throwing — degrading to the coarser
        // global serialisation this attribute replaced, never crashing the job. The claim-first
        // ledger insert still guarantees correctness regardless.
        if (job.Args is { Count: > 0 } args
            && _orgArgumentIndex < args.Count
            && args[_orgArgumentIndex] is Guid orgId)
        {
            return $"poll:{job.Type.Name}.{job.Method.Name}:{orgId:N}";
        }

        return $"poll:{job.Type.Name}.{job.Method.Name}";
    }
}
