using System.Reflection;
using FluentAssertions;
using Hangfire;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Guards the concurrency lock on the recurring cross-tenant sweep jobs. Two overlapping runs of
/// the same sweep duplicate audit rows (StuckRequeued / DeliverySlaBreached) and lose the
/// RequeueCount read-modify-write.
///
/// <para>On OSS Hangfire, <see cref="DisableConcurrentExecutionAttribute"/> keys its distributed
/// lock on the job TYPE + METHOD ONLY — never on the job's arguments (per-argument mutexing needs
/// the paid Hangfire.Pro <c>[Mutex]</c>; see PerOrderDistributedMutexAttribute). These sweeps take
/// no arguments and are cross-tenant by nature, so a per-method lock is exactly the right semantic —
/// unlike the per-ORG poll children, which take an orgId argument and therefore use
/// <c>[PerOrgDistributedMutex]</c> instead (see PollJobConcurrencyGuardTests).</para>
///
/// A distributed lock cannot be unit-tested, so this asserts the attribute is present.
///
/// <para>NOT every recurring job belongs here, and the omissions are deliberate. A guard earns its
/// place only where two overlapping runs cause real harm: duplicate audit/exception rows, a lost
/// read-modify-write, a double external side-effect, or double billing. Audited 2026-07-16 and
/// left unguarded on evidence — do NOT add them to this Theory without re-doing that work:
/// <list type="bullet">
///   <item>WorkerHeartbeatJob — writes nothing but a log line.</item>
///   <item>WorkerHealthAlertJob — no DB writes; its Sentry alert is already de-duplicated by the
///     lock-protected singleton WorkerHealthAlertState, so a guard would buy nothing.</item>
///   <item>DataRetentionSweepJob — pure atomic ExecuteDeleteAsync; no read-modify-write to lose.</item>
///   <item>BlobRetentionSweepJob — SELECT-then-act, but storage deletes are idempotent on a missing
///     key and the purge flag is a constant write, so the only racing effect is an inflated stat.</item>
///   <item>Email/S3/SftpPollingJob — dispatcher parents; the children hold [PerOrgDistributedMutex],
///     which SERIALISES rather than drops, so a duplicate child skips on the committed ledger claim.</item>
/// </list></para>
/// </summary>
public class SweepJobConcurrencyGuardTests
{
    [Theory]
    [InlineData(typeof(StuckOrderDetectionJob))]
    [InlineData(typeof(DeliverySlaSweepJob))]
    [InlineData(typeof(StuckDeliveryDetectionJob))]
    [InlineData(typeof(StrandedFailedDeliveryDetectionJob))]
    [InlineData(typeof(StrandedReadyDeliveryDetectionJob))]
    [InlineData(typeof(BillingReconciliationJob))]
    public void ExecuteAsync_HasDisableConcurrentExecution(Type jobType)
    {
        var method = jobType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{jobType.Name} must expose ExecuteAsync");

        var attr = method!.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        attr.Should().NotBeNull(
            $"{jobType.Name}.ExecuteAsync must be guarded by [DisableConcurrentExecution] so two " +
            "overlapping sweeps cannot double-write audit rows");
    }
}
