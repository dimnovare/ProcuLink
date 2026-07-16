using System.Reflection;
using FluentAssertions;
using Hangfire;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Guards the concurrency lock on the pull-ingress child jobs: two children polling the same org
/// concurrently would both pass the check-then-act dedupe, and (with the claim-first ledger
/// insert) this closes the concurrent-duplicate-import window.
///
/// <para><b>Keying (corrected 2026-07-16):</b> on OSS Hangfire this attribute keys the distributed
/// lock on the job TYPE + METHOD ONLY — it does NOT include the job's arguments, so this is a
/// GLOBAL lock per child job type, not a per-org one. Per-argument mutexing requires the paid
/// Hangfire.Pro <c>[Mutex]</c> (see <c>PerOrderDistributedMutexAttribute</c>, which exists for
/// exactly this reason). A global lock strictly contains the per-org lock the duplicate-import
/// argument needs, so correctness is unaffected — but every org's polling serialises through one
/// lock per channel, which is a throughput ceiling as org count grows. Tracked separately.</para>
/// </summary>
public class PollJobConcurrencyGuardTests
{
    [Theory]
    [InlineData(typeof(SftpPollOrgJob))]
    [InlineData(typeof(S3PollOrgJob))]
    [InlineData(typeof(EmailPollOrgJob))]
    public void ExecuteAsync_HasDisableConcurrentExecution(Type jobType)
    {
        var method = jobType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{jobType.Name} must expose ExecuteAsync");

        var attr = method!.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        attr.Should().NotBeNull(
            $"{jobType.Name}.ExecuteAsync must be guarded by [DisableConcurrentExecution] so two children " +
            "for the same org cannot poll concurrently");
    }
}
