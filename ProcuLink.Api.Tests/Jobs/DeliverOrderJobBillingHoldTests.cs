using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Audit FINDING 5: when the org can't process orders at delivery time, DeliverOrderJob must NOT
/// silently return (stranding the order in ready_to_deliver forever). It correctly does NOT
/// deliver, but moves the order to the explicit, re-drivable <c>delivery_held</c> status via
/// <see cref="IDeliveryService.HoldForBillingAsync"/>.
/// </summary>
public class DeliverOrderJobBillingHoldTests
{
    [Fact]
    public async Task ExecuteAsync_BillingCannotProcess_HoldsOrder_DoesNotDispatch()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.HoldForBillingAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        var job = new DeliverOrderJob(
            delivery.Object,
            billing.Object,
            new Mock<IBackgroundJobClient>().Object,
            NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        // Held explicitly — not silently stranded.
        delivery.Verify(d => d.HoldForBillingAsync(orgId, orderId, It.IsAny<CancellationToken>()), Times.Once);
        // And it did NOT deliver for the non-paying org.
        delivery.Verify(
            d => d.DispatchArtifactAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BillingCanProcess_DispatchesAndDoesNotHold()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(true, null));

        var job = new DeliverOrderJob(
            delivery.Object,
            billing.Object,
            new Mock<IBackgroundJobClient>().Object,
            NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        delivery.Verify(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()), Times.Once);
        delivery.Verify(d => d.HoldForBillingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
