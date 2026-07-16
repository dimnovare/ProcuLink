using System.Reflection;
using FluentAssertions;
using Hangfire;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Guards the concurrency lock on the pull-ingress child jobs: two children polling the same org
/// concurrently would both pass the check-then-act dedupe, and (with the claim-first ledger
/// insert) this closes the concurrent-duplicate-import window.
///
/// <para><b>Keying:</b> these children carry <see cref="PerOrgDistributedMutexAttribute"/>, which
/// keys its storage-backed distributed lock on the job's orgId ARGUMENT
/// (<c>poll:{JobType}.{Method}:{orgId}</c>) — so two activations for the same org serialise, while
/// two DIFFERENT orgs never contend. They must NOT carry the OSS
/// <see cref="DisableConcurrentExecutionAttribute"/>, which keys on job TYPE + METHOD ONLY and
/// would serialise every tenant's polling for a channel through one global lock (a throughput
/// ceiling: one slow endpoint stalls every other org). Per-org granularity is correct and
/// sufficient because the claim-first ledger insert + unique index — not the lock — is what
/// guarantees exactly-one-order; the lock is defence-in-depth / TOCTOU-narrowing.</para>
///
/// <para>The lock itself can't be exercised in a unit test, so this asserts the attribute wiring;
/// <see cref="PerOrgDistributedMutexResourceKeyTests"/> pins the per-org resource key itself.</para>
/// </summary>
public class PollJobConcurrencyGuardTests
{
    [Theory]
    [InlineData(typeof(SftpPollOrgJob))]
    [InlineData(typeof(S3PollOrgJob))]
    [InlineData(typeof(EmailPollOrgJob))]
    public void ExecuteAsync_HasPerOrgDistributedMutex_KeyedOnOrgIdArgument(Type jobType)
    {
        var method = jobType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{jobType.Name} must expose ExecuteAsync");

        var attr = method!.GetCustomAttribute<PerOrgDistributedMutexAttribute>();
        attr.Should().NotBeNull(
            $"{jobType.Name}.ExecuteAsync must be guarded by [PerOrgDistributedMutex] so two children " +
            "for the same org cannot poll concurrently — while two different orgs still poll in parallel");

        // orgId is the FIRST parameter, so the mutex must key on argument index 0.
        method!.GetParameters()[0].Name.Should().Be("orgId",
            $"{jobType.Name}.ExecuteAsync's first parameter must be the orgId the mutex keys on");
    }

    /// <summary>
    /// The global lock must be GONE, not merely supplemented: leaving
    /// <c>[DisableConcurrentExecution]</c> alongside the per-org mutex would re-impose the
    /// cross-tenant throughput ceiling the per-org lock exists to remove.
    /// </summary>
    [Theory]
    [InlineData(typeof(SftpPollOrgJob))]
    [InlineData(typeof(S3PollOrgJob))]
    [InlineData(typeof(EmailPollOrgJob))]
    public void ExecuteAsync_HasNoGlobalDisableConcurrentExecution(Type jobType)
    {
        var method = jobType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{jobType.Name} must expose ExecuteAsync");

        // Read attribute METADATA rather than instantiating: DisableConcurrentExecutionAttribute's
        // ctor touches Hangfire's LogProvider, which throws in a bare test context.
        var hasGlobalLock = method!.CustomAttributes
            .Any(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        hasGlobalLock.Should().BeFalse(
            $"{jobType.Name}.ExecuteAsync must NOT carry [DisableConcurrentExecution] — on OSS Hangfire it " +
            "keys on job TYPE + METHOD ONLY (never the arguments), so it serialises EVERY org's polling for " +
            "this channel through one lock: one slow/hung tenant endpoint blocks all other tenants for up to " +
            "the lock timeout. [PerOrgDistributedMutex] replaces it with a per-org key");
    }
}
