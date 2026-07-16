using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// The first-deliver twin of <c>RetryDeliveryJob</c>'s unbounded-loop guard. DeliverOrderJob runs the
/// same decision (not 4xx + under the cap → schedule a backoff retry), so it must make the same
/// distinction: a <see cref="DeliveryOutcome.NotAttempted"/> result never reached a dispatcher and
/// wrote no attempt row, so seeding the retry queue from it just hands RetryDeliveryJob an order it
/// can only bow out of.
/// </summary>
public class DeliverOrderJobNoDispatchTests
{
    [Fact]
    public async Task ExecuteAsync_NoDispatchAttempted_DoesNotSeedRetryQueue()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        // Nothing dispatched (e.g. the artifact vanished, or another worker holds the claim):
        // no attempt row, no response code — indistinguishable from a transient failure without Outcome.
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Order artifact not found.", Outcome: DeliveryOutcome.NotAttempted));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().BeEmpty("nothing was dispatched, so there is no failure for the retry queue to own");
    }

    [Fact]
    public async Task ExecuteAsync_TransientDispatchFailure_StillSeedsRetryQueue()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        // A real 5xx: the payload reached the supplier and an attempt row exists → retryable.
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 503", 503));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().HaveCount(1, "a transient failure is exactly what the backoff queue is for");
        jobs.Captured.Single().State.Should().BeOfType<ScheduledState>();
    }

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
