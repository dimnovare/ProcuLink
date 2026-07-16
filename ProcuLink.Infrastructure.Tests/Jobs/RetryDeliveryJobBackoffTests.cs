using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// Group O reliability — the automatic delivery retry queue. Verifies that
/// <see cref="RetryDeliveryJob"/> schedules the next attempt at the configured exponential
/// backoff after a transient failure, stops on a 4xx supplier rejection, and stops once the
/// attempt cap is reached (dead-letter already handled inside the service).
/// <see cref="IDeliveryService"/> is mocked so the scheduling decision is asserted in isolation.
/// </summary>
public class RetryDeliveryJobBackoffTests
{
    private static readonly DeliveryReliabilityOptions Options =
        new() { MaxAttempts = 3, BackoffMinutes = new[] { 30, 60, 120 } };

    [Fact]
    public async Task ExecuteAsync_TransientFailureBelowCap_SchedulesRetryAtBackoffDelay()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        // Transient 5xx failure (no 4xx), one attempt now recorded.
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 503", 503));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        var before = DateTime.UtcNow;
        await job.ExecuteAsync(orderId, orgId, default);
        var after = DateTime.UtcNow;

        jobs.Captured.Should().HaveCount(1);
        var state = jobs.Captured.Single().State;
        state.Should().BeOfType<ScheduledState>();
        // After 1 failed attempt the next backoff is BackoffMinutes[0] == 30 min.
        ((ScheduledState)state).EnqueueAt.Should()
            .BeCloseTo(before.AddMinutes(30), TimeSpan.FromMinutes(1))
            .And.BeOnOrAfter(before.AddMinutes(30).AddSeconds(-1));
        _ = after;
    }

    [Fact]
    public async Task ExecuteAsync_SecondTransientFailure_UsesSecondBackoffStep()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 500", 500));
        // Two attempts recorded → next backoff step is BackoffMinutes[1] == 60 min.
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(2);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        var before = DateTime.UtcNow;
        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1);
        ((ScheduledState)jobs.Captured.Single().State).EnqueueAt
            .Should().BeCloseTo(before.AddMinutes(60), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ExecuteAsync_SupplierRejection_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        // 4xx = explicit rejection → terminal, never auto-retried.
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 422", 422));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AtAttemptCap_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Maximum delivery attempts reached — order moved to dead-letter."));
        // Cap reached → service already dead-lettered; the queue must stop.
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(3);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty();
    }

    // The park's whole purpose is that a HUMAN decides. RetryDeliveryAsync refuses
    // 'delivery_unconfirmed' as non-retryable and persists no new attempt row on that early
    // return, so scheduling a backoff retry anyway would see the attempt count never advance —
    // the queue would reschedule itself at the same delay FOREVER (never re-sending, never
    // dead-lettering, never resolving). A parked result must leave the backoff queue untouched.
    [Fact]
    public async Task ExecuteAsync_ParkedResult_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Delivery unconfirmed…", ResponseCode: null, Parked: true));

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty("a parked delivery waits for an operator, never for the backoff queue");
        delivery.Verify(d => d.CountDeliveryAttemptsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "the parked guard returns before the attempt-count read");
    }

    // Regression guard: an ordinary transient failure (Parked defaults to false) must STILL
    // be retried — the park guard must not swallow normal backoff scheduling.
    [Fact]
    public async Task ExecuteAsync_OrdinaryTransientFailure_StillSchedulesRetry()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "connection reset", ResponseCode: null));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1, "a normal transient failure still enters the backoff queue — the park must not break retries");
    }

    [Fact]
    public async Task ExecuteAsync_Success_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(true, null, 202));

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty();
        delivery.Verify(d => d.CountDeliveryAttemptsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "a delivered order needs no backoff bookkeeping");
    }

    /// <summary>
    /// Captures every <see cref="IBackgroundJobClient.Create"/> call so the scheduled state
    /// (and its backoff delay) raised by the <c>Schedule&lt;T&gt;</c> extension can be asserted.
    /// </summary>
    private sealed class CapturingJobClient : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> Captured { get; } = new();

        public string Create(Job job, IState state)
        {
            Captured.Add((job, state));
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
