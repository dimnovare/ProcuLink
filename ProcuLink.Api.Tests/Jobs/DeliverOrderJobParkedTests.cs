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
/// <see cref="DeliveryService.ParkUnconfirmedAsync"/> (a crash-recovery re-drive on a channel
/// that cannot de-duplicate) leaves the order at 'delivery_unconfirmed', a status
/// <c>RetryDeliveryAsync</c> refuses as non-retryable — that early return persists NO new
/// attempt row. If <see cref="DeliverOrderJob"/> scheduled an automatic retry anyway, the
/// resulting <see cref="RetryDeliveryJob"/> chain would see the attempt count never advance:
/// the backoff queue would reschedule itself at the SAME delay forever, never re-sending,
/// never dead-lettering, never resolving. The order instead waits for an operator
/// ("Send again" / "Mark as delivered").
/// </summary>
public class DeliverOrderJobParkedTests
{
    [Fact]
    public async Task ExecuteAsync_ParkedResult_DoesNotScheduleRetry()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Delivery unconfirmed…", ResponseCode: null, Parked: true));

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

    // Regression guard: an ordinary transient failure (Parked defaults to false) must STILL
    // be handed to the backoff queue — the park guard must not swallow normal retry scheduling.
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
