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
/// WP-19 — the FIRST-failure half of the 4xx split.
///
/// <para>Three call sites carried the same hand-written <c>responseCode is &gt;= 400 and &lt;= 499</c>
/// and each concluded "supplier rejection, terminal": <c>DeliveryService</c> (which status to
/// write), this job (whether to seed the backoff queue) and <c>RetryDeliveryJob</c> (whether to
/// schedule the next step). An expired API key, a moved endpoint or a rate limit therefore parked a
/// deliverable PO in <c>rejected_by_supplier</c> — a status with no outgoing transitions and no
/// operator control that could move it.</para>
///
/// <para>This job runs the same decision <c>RetryDeliveryJob</c> does, so the two must agree; the
/// tests are deliberately mirrored (<c>RetryDeliveryJobBackoffTests</c>) so a change to one gate
/// alone shows up as a red test rather than as an order that stops moving.</para>
/// </summary>
public class DeliverOrderJob4xxSplitTests
{
    [Theory]
    [InlineData(401)] // credentials expired or rotated
    [InlineData(403)] // credentials fine, this account may not post orders here
    [InlineData(404)] // the endpoint moved
    [InlineData(408)] // the endpoint did not answer in time
    [InlineData(429)] // rate limited
    public async Task ExecuteAsync_TransportRefusal_SeedsTheRetryQueue(int code)
    {
        var (delivery, billing, orgId, orderId, artifactId) = Arrange(
            new DeliveryResult(false, $"HTTP {code}", code), attemptsMade: 1);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().HaveCount(1,
            $"HTTP {code} says the supplier's system refused the REQUEST, not the order — the " +
            "backoff queue is what carries it to a delivery or an honest dead-letter instead of " +
            "abandoning it in a status nothing can move");
        jobs.Captured.Single().State.Should().BeOfType<ScheduledState>();
    }

    [Fact]
    public async Task ExecuteAsync_BareBadRequest_FromAChannelThatCouldHaveHeard_SeedsTheRetryQueue()
    {
        // Bare, a 400 is indistinguishable from a bad URL or a malformed header. Only a 400 that
        // carries the supplier's reason is a judgement on the document.
        //
        // SupplierReasonObservable is the load-bearing half of "bare": it says the channel READ the
        // response and found nothing there. Set explicitly, because a result nobody stamped means
        // the opposite — "we never looked" — and gets the conservative verdict instead. DeliveryService
        // stamps it from the dispatcher in production; here the job is under test on its own.
        var (delivery, billing, orgId, orderId, artifactId) = Arrange(
            new DeliveryResult(false, "HTTP 400", 400, SupplierReasonObservable: true), attemptsMade: 1);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().HaveCount(1);
    }

    /// <summary>
    /// …and the same 400 from a channel that CANNOT read the body does not, because "bare" was never
    /// established. Three dispatchers were in that position for the whole of WP-19 — erp_erply,
    /// erp_directo and email — and every one of their 400s took the branch above, re-POSTing to a
    /// live endpoint up to the attempt cap. They capture the body now; this pins the answer for the
    /// dispatcher that does not exist yet.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BareBadRequest_FromAChannelThatCouldNotHear_DoesNotSeedTheRetryQueue()
    {
        var (delivery, billing, orgId, orderId, artifactId) = Arrange(
            new DeliveryResult(false, "HTTP 400", 400), attemptsMade: 1);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().BeEmpty(
            "an unexplained refusal we could not have heard an explanation for is not proof the " +
            "supplier gave none — and the wrong guess here costs real requests against a live " +
            "endpoint, on channels that are either the production email path or ResendSafety.Unsafe");
    }

    [Theory]
    [InlineData(422, null)]
    [InlineData(400, "{\"error\":\"unknown buyer code BC-9\"}")]
    public async Task ExecuteAsync_BusinessRejection_DoesNotSeedTheRetryQueue(int code, string? body)
    {
        var (delivery, billing, orgId, orderId, artifactId) = Arrange(
            new DeliveryResult(false, $"HTTP {code}", code, ResponseBody: body,
                SupplierReasonObservable: true), attemptsMade: 1);

        var jobs = new CapturingJobClient();
        var job = new DeliverOrderJob(delivery.Object, billing.Object, jobs, NullLogger<DeliverOrderJob>.Instance);

        await job.ExecuteAsync(orderId, orgId, artifactId, requireAutoDeliver: true, CancellationToken.None);

        jobs.Captured.Should().BeEmpty(
            "the supplier read the document and refused it — the cure is a corrected document " +
            "(resolve / re-transform), which the operator drives, not a re-send of the same bytes");
    }

    private static (Mock<IDeliveryService> Delivery, Mock<IBillingService> Billing, Guid OrgId, Guid OrderId, Guid ArtifactId)
        Arrange(DeliveryResult result, int attemptsMade)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CanProcessOrdersAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.DispatchArtifactAsync(orgId, orderId, artifactId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(attemptsMade);

        return (delivery, billing, orgId, orderId, artifactId);
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
