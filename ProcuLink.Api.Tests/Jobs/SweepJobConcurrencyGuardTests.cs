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
/// no arguments and are global by nature, so a per-method lock is exactly the right semantic.</para>
///
/// A distributed lock cannot be unit-tested, so this asserts the attribute is present — the same
/// approach PollJobConcurrencyGuardTests takes for the poll children.
/// </summary>
public class SweepJobConcurrencyGuardTests
{
    [Theory]
    [InlineData(typeof(StuckOrderDetectionJob))]
    [InlineData(typeof(DeliverySlaSweepJob))]
    [InlineData(typeof(StuckDeliveryDetectionJob))]
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
