using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// A recurring job whose class was deleted keeps its row in Hangfire storage and fails on every tick
/// until the row is removed, so <c>Worker.StartAsync</c> removes each retired id. This pins the list
/// and that no retired id is also registered.
/// </summary>
public class RetiredRecurringJobsTests
{
    [Fact]
    public async Task StartAsync_RemovesEveryRetiredId_AndRegistersNone_OfThem()
    {
        var jobs = new RecordingRecurringJobManager();
        var worker = new ProcuLink.Worker.Worker(NullLogger<ProcuLink.Worker.Worker>.Instance, jobs);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        ProcuLink.Worker.Worker.RetiredRecurringJobIds.Should().BeEquivalentTo(
            new[] { "email-polling", "sftp-polling", "s3-polling", "catalog-sync" });
        jobs.Removed.Should().BeEquivalentTo(ProcuLink.Worker.Worker.RetiredRecurringJobIds);
        jobs.Added.Should().NotBeEmpty("StartAsync must really have registered something");
        jobs.Added.Intersect(ProcuLink.Worker.Worker.RetiredRecurringJobIds).Should().BeEmpty();
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<string> Added { get; } = new();
        public List<string> Removed { get; } = new();

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Added.Add(recurringJobId);

        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);

        public void Trigger(string recurringJobId) { }
    }
}
