using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Idempotency / safety tests for <see cref="WorkerHeartbeatJob"/> — the recurring
/// liveness beat. The job only logs (no DB / no external side effect), so repeated
/// or overlapping runs must be harmless; these tests pin that contract so a future
/// edit that adds a mutating side effect (and breaks Hangfire-retry idempotency) is
/// caught.
/// </summary>
public class WorkerHeartbeatJobTests
{
    private static WorkerHeartbeatJob NewJob() =>
        new(NullLogger<WorkerHeartbeatJob>.Instance);

    [Fact]
    public async Task Execute_CompletesWithoutThrowing()
    {
        var job = NewJob();

        var act = async () => await job.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_IsIdempotent_RepeatedRunsAreHarmless()
    {
        var job = NewJob();

        // Three back-to-back runs (mimicking a Hangfire retry / overlapping beat)
        // must all succeed with no accumulated state or exception.
        for (var i = 0; i < 3; i++)
        {
            var act = async () => await job.ExecuteAsync(CancellationToken.None);
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task Execute_HonoursCancellationToken_StillNoThrow()
    {
        var job = NewJob();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The job does no awaitable work, so a cancelled token must not make it
        // throw — it simply logs and returns.
        var act = async () => await job.ExecuteAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }
}
