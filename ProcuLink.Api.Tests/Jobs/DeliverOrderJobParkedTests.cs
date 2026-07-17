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
/// Task 3B — a parked delivery must never enter the automatic backoff queue.
/// <see cref="DeliveryService.ParkUnconfirmedAsync"/> (a crash-recovery re-drive on a channel that
/// cannot de-duplicate) leaves the order at 'delivery_unconfirmed' and reports
/// <see cref="DeliveryOutcome.NotRetryable"/>. The park is the case that forced that marker to mean
/// "someone outside the queue owns this" rather than "no attempt row was written": the park DOES
/// finalise its attempt row, so the unbounded-loop reasoning that covers the other NotRetryable
/// paths would not have stopped the queue here. What stops it is that re-sending an outcome nobody
/// observed risks a DUPLICATE PO — the order waits for an operator ("Send again" / "Mark as
/// delivered"), never for the backoff queue.
///
/// <para>Companion to <see cref="DeliverOrderJobNoDispatchTests"/>, which pins the same guard for
/// the no-attempt-row cases: same mechanism, deliberately different scenarios.</para>
/// </summary>
public class DeliverOrderJobParkedTests
{
    [Fact]
    public async Task ExecuteAsync_ParkedOrderResult_DoesNotScheduleRetry()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Delivery unconfirmed…", ResponseCode: null,
                    Outcome: DeliveryOutcome.NotRetryable));

        var jobs = new Mock<IBackgroundJobClient>();

        var job = new DeliverOrderJob(
            delivery.Object,
            billing.Object,
            jobs.Object,
            NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never(),
            "a parked delivery waits for an operator, never for the backoff queue");
    }

    // Regression guard: an ordinary transient failure (Outcome defaults to Dispatched) must STILL
    // be handed to the backoff queue — the NotRetryable guard must not swallow normal retry
    // scheduling.
    [Fact]
    public async Task ExecuteAsync_OrdinaryTransientFailure_StillSchedulesRetry()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "connection reset", ResponseCode: null));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new Mock<IBackgroundJobClient>();

        var job = new DeliverOrderJob(
            delivery.Object,
            billing.Object,
            jobs.Object,
            NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once(),
            "a normal transient failure still enters the backoff queue — the park must not break retries");
    }
}
