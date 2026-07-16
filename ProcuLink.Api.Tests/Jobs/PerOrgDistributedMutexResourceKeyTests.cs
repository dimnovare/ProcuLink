using FluentAssertions;
using Hangfire.Common;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// A job type whose first argument is NOT a Guid, used to prove the resource-key fallback below.
/// Top-level (not nested) so Hangfire's <see cref="Job.FromExpression{T}"/> validation accepts it.
/// </summary>
public sealed class NonGuidArgProbeJob
{
    public Task ExecuteAsync(string notAnOrgId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The POINT of <see cref="PerOrgDistributedMutexAttribute"/>: the distributed-lock resource it
/// acquires must be keyed on the job's orgId ARGUMENT, so two different orgs never contend.
///
/// <para>The lock itself is storage-backed and can't be exercised in a unit test, but the resource
/// key is pure and fully testable — and the key is the entire difference between the old GLOBAL
/// <c>[DisableConcurrentExecution]</c> (type+method only) and the per-org lock. These tests pin it.</para>
/// </summary>
public class PerOrgDistributedMutexResourceKeyTests
{
    private static readonly PerOrgDistributedMutexAttribute Attr = new(orgArgumentIndex: 0, timeoutSeconds: 300);

    private static Job SftpJobFor(Guid orgId) =>
        Job.FromExpression<SftpPollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None));

    /// <summary>
    /// THE throughput fix: org A's SFTP poll and org B's SFTP poll take DIFFERENT locks, so a slow
    /// or hung endpoint on org A cannot block org B's polling. Under the old global lock these two
    /// resources were identical.
    /// </summary>
    [Fact]
    public void BuildResource_TwoDifferentOrgs_ProduceDifferentResources()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var resourceA = Attr.BuildResource(SftpJobFor(orgA));
        var resourceB = Attr.BuildResource(SftpJobFor(orgB));

        resourceA.Should().NotBe(resourceB,
            "two orgs polling the same channel must take DIFFERENT distributed locks — one slow " +
            "tenant's endpoint must never block another tenant's poll");
        resourceA.Should().Contain(orgA.ToString("N"), "the resource must key on the orgId argument");
        resourceB.Should().Contain(orgB.ToString("N"), "the resource must key on the orgId argument");
    }

    /// <summary>
    /// The correctness half: two activations for the SAME org DO contend, exactly as the old global
    /// lock made them — this is the TOCTOU-narrowing guard over the claim-first ledger insert.
    /// </summary>
    [Fact]
    public void BuildResource_SameOrgTwice_ProducesIdenticalResource()
    {
        var orgId = Guid.NewGuid();

        var first = Attr.BuildResource(SftpJobFor(orgId));
        var second = Attr.BuildResource(SftpJobFor(orgId));

        first.Should().Be(second,
            "two SftpPollOrgJob activations for the SAME org must take the SAME lock so they " +
            "cannot poll concurrently");
    }

    /// <summary>
    /// Channels are independent: an org's SFTP poll and its IMAP poll hit different endpoints and
    /// different ledgers, so they must not serialise against each other.
    /// </summary>
    [Fact]
    public void BuildResource_SameOrgDifferentChannels_ProduceDifferentResources()
    {
        var orgId = Guid.NewGuid();

        var sftp = Attr.BuildResource(SftpJobFor(orgId));
        var s3 = Attr.BuildResource(Job.FromExpression<S3PollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None)));
        var email = Attr.BuildResource(Job.FromExpression<EmailPollOrgJob>(j => j.ExecuteAsync(orgId, CancellationToken.None)));

        new[] { sftp, s3, email }.Should().OnlyHaveUniqueItems(
            "each channel polls a different endpoint for the org — they must not serialise against each other");
    }

    /// <summary>
    /// Defensive degradation, mirroring PerOrderDistributedMutexAttribute: an unexpected argument
    /// shape must fall back to a coarser method-level lock, never throw. Correctness still rests on
    /// the claim-first ledger insert, so a coarser lock is safe; a crashing filter would not be.
    /// </summary>
    [Fact]
    public void BuildResource_FirstArgumentIsNotAGuid_FallsBackToMethodLevelResource()
    {
        var job = Job.FromExpression<NonGuidArgProbeJob>(j => j.ExecuteAsync("not-an-org-id", CancellationToken.None));

        var act = () => Attr.BuildResource(job);

        act.Should().NotThrow("an unexpected argument shape must degrade to a coarser lock, never crash the job");
        Attr.BuildResource(job).Should().Contain(nameof(NonGuidArgProbeJob),
            "the fallback must still be a stable method-level resource");
    }

    /// <summary>
    /// An out-of-range configured index must degrade the same way rather than throwing.
    /// </summary>
    [Fact]
    public void BuildResource_ArgumentIndexOutOfRange_FallsBackToMethodLevelResource()
    {
        var attr = new PerOrgDistributedMutexAttribute(orgArgumentIndex: 7, timeoutSeconds: 300);
        var job = SftpJobFor(Guid.NewGuid());

        var act = () => attr.BuildResource(job);

        act.Should().NotThrow();
        attr.BuildResource(job).Should().Contain(nameof(SftpPollOrgJob));
    }
}
