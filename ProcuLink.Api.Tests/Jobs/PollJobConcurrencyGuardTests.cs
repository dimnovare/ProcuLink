using System.Reflection;
using FluentAssertions;
using Hangfire;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Guards the per-org concurrency lock on the pull-ingress child jobs. Hangfire's
/// <see cref="DisableConcurrentExecutionAttribute"/> keys the distributed lock on the method +
/// its arguments; each child job takes <c>orgId</c> as its first argument, so the lock is
/// effectively per-organisation — two children for the SAME org can never overlap, which (with
/// the claim-first ledger insert) closes the concurrent-duplicate-import window.
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
