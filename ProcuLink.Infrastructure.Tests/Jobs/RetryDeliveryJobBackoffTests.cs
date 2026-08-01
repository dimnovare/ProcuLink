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
        // After 1 failed attempt the next backoff is BackoffMinutes[0] == 30 min, plus up to
        // RetryJitterPercent (20%) on top — the band, not a point, because a fixed offset is what
        // re-fires a whole failed batch as one burst. Asserted as [step, step + jitter] so the test
        // still pins that the step is 30 and that jitter is upward-only.
        ((ScheduledState)state).EnqueueAt.Should()
            .BeOnOrAfter(before.AddMinutes(30).AddSeconds(-1),
                "jitter is upward-only — a retry must never fire before the scheduled step")
            .And.BeOnOrBefore(before.AddMinutes(36).AddSeconds(1),
                "and never beyond the declared 20% band above it");
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
        // Second step (60 min) + up to 20% jitter — see the band comment on the first-step test.
        ((ScheduledState)jobs.Captured.Single().State).EnqueueAt
            .Should().BeOnOrAfter(before.AddMinutes(60).AddSeconds(-1))
            .And.BeOnOrBefore(before.AddMinutes(72).AddSeconds(1));
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

    /// <summary>
    /// WP-19, the retry half of the split. The queue used to stop on ANY 4xx, so an expired API key
    /// or a moved endpoint abandoned an order that a key rotation would have delivered — and the
    /// status it was abandoned in (<c>rejected_by_supplier</c>) had no exit either. A transport
    /// refusal is now an ordinary failure: keep the backoff running so the order reaches a delivery
    /// or an honest dead-letter, both of which an operator can act on.
    ///
    /// <para>This is one half of the difference assertion. Its partner is
    /// <c>DeliveryServiceRejectionTests.DispatchArtifactAsync_TransportRefusal_…</c>, which pins the
    /// STATUS written for the same code. Reverting either call site to
    /// <c>responseCode is &gt;= 400 and &lt;= 499</c> turns one of the two red.</para>
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(429)]
    public async Task ExecuteAsync_TransportRefusal_StillSchedulesTheNextBackoffStep(int code)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, $"HTTP {code}", code));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1,
            $"HTTP {code} is the supplier's system refusing the REQUEST, not a judgement on the " +
            "order — the backoff queue is exactly what carries it to a delivery or a dead-letter");
        jobs.Captured.Single().State.Should().BeOfType<ScheduledState>();
    }

    /// <summary>
    /// The reserved half, and the reason the split cannot collapse back to a range check: a bare 400
    /// is indistinguishable from a bad URL or a malformed header, so it keeps retrying…
    ///
    /// <para><c>SupplierReasonObservable</c> is what makes "bare" a fact rather than an assumption —
    /// the channel read the response and found nothing. Its absence means the opposite (see the
    /// sibling test below), which is why it is set explicitly here.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BareBadRequest_FromAChannelThatCouldHaveHeard_StillSchedules()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 400", 400, SupplierReasonObservable: true));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1);
    }

    /// <summary>
    /// …and the SAME bare 400 from a channel that could not read the body does NOT keep retrying:
    /// "the supplier said nothing" was never established, so the queue must not spend real requests
    /// on the assumption. This is the state erp_erply, erp_directo and email were in for the whole
    /// of WP-19; the guard now belongs to the class, not to those three.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BareBadRequest_FromAChannelThatCouldNotHear_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "HTTP 400", 400));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty(
            "the retry gate and the status decision are one decision — this must agree with " +
            "DeliveryServiceRejectionTests' verdict for the same shape, or the order is retried " +
            "into a status the delivery claim refuses");
    }

    /// <summary>…while the SAME 400 carrying the supplier's reason is a business rejection and stops.</summary>
    [Fact]
    public async Task ExecuteAsync_BadRequestCarryingASupplierReason_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(
                    false, "HTTP 400", 400, ResponseBody: "{\"error\":\"unknown buyer code BC-9\"}"));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty(
            "the supplier read the document and said what is wrong with it — re-sending the same " +
            "bytes cannot help, and the delivery claim refuses the resulting status anyway");
    }

    /// <summary>
    /// A bulk of orders that fail together must NOT come back together.
    ///
    /// <para><b>The defect.</b> <see cref="DeliveryReliabilityOptions.BackoffMinutes"/> is a FIXED
    /// schedule — 30 → 60 → 120 — with no jitter anywhere in the repository. So N orders for one
    /// supplier that fail in the same minute (a rate limit, a credential rotation, a brief outage:
    /// every cause that fails a batch fails all of it) are all scheduled at the same offset, and the
    /// whole batch re-fires as one synchronised burst — three times, at three fixed offsets. The
    /// only thing standing between the supplier and N simultaneous deliveries is the Worker's
    /// <c>WorkerCount = 10</c>, which is a concurrency limit, not a rate limit. This is the one
    /// failure mode here that grows with order volume rather than staying per-order.</para>
    ///
    /// <para>Asserted as SPREAD rather than as any particular delay, so the test states the property
    /// that matters (the burst is broken up) and not an implementation of it.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ABulkFailingTogether_IsSpreadAcrossAWindow_NotOneBurst()
    {
        const int bulk = 24;
        var scheduled = new List<DateTime>();

        for (var i = 0; i < bulk; i++)
        {
            var orgId = Guid.NewGuid();
            var orderId = Guid.NewGuid();

            var delivery = new Mock<IDeliveryService>();
            // The same transient failure for every order in the bulk — one supplier, one outage.
            delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DeliveryResult(false, "HTTP 503", 503));
            delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(1);

            var jobs = new CapturingJobClient();
            var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

            await job.ExecuteAsync(orderId, orgId, default);

            scheduled.Add(((ScheduledState)jobs.Captured.Single().State).EnqueueAt);
        }

        var spread = scheduled.Max() - scheduled.Min();

        spread.Should().BeGreaterThan(TimeSpan.FromMinutes(1),
            $"{bulk} orders that failed together were all scheduled within {spread.TotalSeconds:F1}s " +
            "of each other, so they re-fire at the supplier as a single synchronised burst — and " +
            "then twice more, at the next two fixed offsets. A jittered backoff spreads the retry " +
            "across a window instead of re-creating the thundering herd that caused the failure");
    }

    /// <summary>
    /// A rate-limited supplier that tells us WHEN to come back is obeyed. Nothing in this repository
    /// read <c>Retry-After</c> before WP-19's follow-up — a 429 was retried on our own fixed schedule
    /// regardless of what the endpoint asked for, which is how a rate limit turns into three more
    /// rate limits.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SupplierAskedUsToWaitLonger_HonoursIt()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(
                    false, "HTTP 429", 429, SupplierReasonObservable: true,
                    RetryAfter: TimeSpan.FromMinutes(90)));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        var before = DateTime.UtcNow;
        await job.ExecuteAsync(orderId, orgId, default);

        ((ScheduledState)jobs.Captured.Single().State).EnqueueAt
            .Should().BeOnOrAfter(before.AddMinutes(90).AddSeconds(-1),
                "the endpoint asked for 90 minutes; coming back after our own 30-minute step would " +
                "be a third rate-limited request we were explicitly told not to make");
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

    // The park's whole purpose is that a HUMAN decides. It arrives as NotRetryable — the scenario the
    // marker was widened to cover, and the one case of it whose attempt count DOES advance, so the
    // unbounded-loop argument alone would not have stopped the queue here. Re-sending an unobserved
    // outcome on a channel that cannot de-duplicate risks a duplicate PO, so the backoff queue must
    // stay out of it entirely: the order waits for "Send again" / "Mark as delivered".
    [Fact]
    public async Task ExecuteAsync_ParkedOrderResult_DoesNotSchedule()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Delivery unconfirmed…", ResponseCode: null,
                    Outcome: DeliveryOutcome.NotRetryable));

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty("a parked delivery waits for an operator, never for the backoff queue");
        delivery.Verify(d => d.CountDeliveryAttemptsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "the parked guard returns before the attempt-count read");
    }

    // Regression guard: an ordinary transient failure (Outcome defaults to Dispatched) must STILL
    // be retried — the NotRetryable guard must not swallow normal backoff scheduling.
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
    /// A TERMINAL non-dispatch (no attempt row, and no later attempt can help) must NOT be
    /// rescheduled. Rescheduling one is an UNBOUNDED loop, not a retry: with no attempt row the
    /// count never advances, so <c>attemptsMade &gt;= maxAttempts</c> never becomes true and every
    /// run schedules the same backoff step forever. Only the Outcome distinguishes this from a real
    /// transient failure — the message text is not a contract and ResponseCode is null for both.
    /// </summary>
    [Theory]
    [InlineData("Order status 'delivery_held' is not retryable.")]
    [InlineData("Order is in dead-letter state — retries are exhausted.")]
    [InlineData("Order not found.")]
    [InlineData("No outbound artifact found. Transform the order before retrying delivery.")]
    public async Task ExecuteAsync_NotRetryable_DoesNotSchedule(string message)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, message, Outcome: DeliveryOutcome.NotRetryable));
        // The count is FROZEN at whatever it was — the cap guard can never fire on this path.
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().BeEmpty(
            "no attempt row was written, so a rescheduled run would repeat this forever");
        delivery.Verify(d => d.CountDeliveryAttemptsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "backoff bookkeeping is meaningless when nothing was dispatched");
    }

    /// <summary>
    /// A LOST CLAIM writes no attempt row either, yet it MUST still be rescheduled — the OPPOSITE of
    /// the terminal case above. That opposition is precisely why the two cannot share one enum
    /// member.
    ///
    /// <para>When the claim holder dies, StuckDeliveryDetectionService re-drives the order but bumps
    /// UpdatedAt to now BEFORE enqueuing, so the re-driven retry finds a still-fresh 'delivering' row
    /// and loses the claim again. This backoff is what carries the order past the reclaim window so a
    /// later attempt CAN claim it. Drop it and a crashed holder's PO is never sent — proven
    /// end-to-end in CrashedHolderRecoveryCompositionPostgresTests.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ClaimLost_StillSchedulesRetry_ItIsTheCrashRecoveryNet()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Delivery for this order is already in progress.",
                    Outcome: DeliveryOutcome.ClaimLost));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1,
            "the reschedule is the ONLY thing that recovers an order whose claim holder crashed");
    }

    /// <summary>
    /// Guards the default: an unmarked <see cref="DeliveryResult"/> must keep the retry queue
    /// running. <see cref="DeliveryOutcome.Dispatched"/> is the safe default — a missed call site
    /// degrades to the old (noisy) behaviour, never to silently abandoning a deliverable order.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TransientFailure_DefaultOutcomeIsDispatched_SchedulesRetry()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var delivery = new Mock<IDeliveryService>();
        delivery.Setup(d => d.RetryDeliveryAsync(orgId, orderId, 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult(false, "Connection refused"));
        delivery.Setup(d => d.CountDeliveryAttemptsAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(delivery.Object, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(orderId, orgId, default);

        jobs.Captured.Should().HaveCount(1);
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
