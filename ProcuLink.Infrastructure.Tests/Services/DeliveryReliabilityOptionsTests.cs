using FluentAssertions;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Group O reliability — backoff-schedule and SLA-window maths for
/// <see cref="DeliveryReliabilityOptions"/>. Pure logic; no infrastructure.
/// </summary>
public class DeliveryReliabilityOptionsTests
{
    [Theory]
    [InlineData(1, 30)]   // after 1 failure → first backoff
    [InlineData(2, 60)]   // after 2 failures → second backoff
    [InlineData(3, 120)]  // after 3 failures → third backoff
    [InlineData(4, 120)]  // beyond the table → clamps to the last step
    [InlineData(99, 120)]
    public void BackoffFor_FollowsExponentialScheduleAndClampsToLastStep(int failedAttempts, int expectedMinutes)
    {
        var options = new DeliveryReliabilityOptions { BackoffMinutes = new[] { 30, 60, 120 } };

        options.BackoffFor(failedAttempts).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BackoffFor_AtOrBelowFirstAttempt_UsesFirstStep(int failedAttempts)
    {
        var options = new DeliveryReliabilityOptions { BackoffMinutes = new[] { 30, 60, 120 } };

        options.BackoffFor(failedAttempts).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void BackoffFor_EmptySchedule_FallsBackTo30Minutes()
    {
        var options = new DeliveryReliabilityOptions { BackoffMinutes = Array.Empty<int>() };

        options.BackoffFor(1).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Defaults_AreThreeAttemptsThirtyMinuteBaseAndTwoHourSla()
    {
        var options = new DeliveryReliabilityOptions();

        options.MaxAttempts.Should().Be(3);
        options.BackoffMinutes.Should().Equal(30, 60, 120);
        options.SlaWindow.Should().Be(TimeSpan.FromMinutes(120));
    }

    [Fact]
    public void SlaWindow_NonPositiveConfiguredValue_FallsBackToTwoHours()
    {
        new DeliveryReliabilityOptions { SlaWindowMinutes = 0 }.SlaWindow
            .Should().Be(TimeSpan.FromMinutes(120));
        new DeliveryReliabilityOptions { SlaWindowMinutes = -10 }.SlaWindow
            .Should().Be(TimeSpan.FromMinutes(120));
    }

    // ── NextRetryDelay: jitter + Retry-After (WP-19 follow-up) ────────────────────────────────

    private static DeliveryReliabilityOptions Standard() =>
        new() { MaxAttempts = 3, BackoffMinutes = new[] { 30, 60, 120 } };

    /// <summary>
    /// Jitter is UPWARD-ONLY. Symmetric jitter would let a retry fire before the documented step —
    /// and, worse, before a <c>Retry-After</c> the supplier explicitly asked for. Being late is
    /// harmless; being early is the thing the supplier's header exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(0.0, 30.0)]  // the floor IS the scheduled step
    [InlineData(0.5, 33.0)]  // +10% of 30
    [InlineData(1.0, 36.0)]  // +20% of 30 — the ceiling
    public void NextRetryDelay_JittersUpwardOnly_FromTheScheduledStep(double sample, double expectedMinutes)
        => Standard().NextRetryDelay(1, supplierRetryAfter: null, jitterSample: sample)
            .Should().BeCloseTo(TimeSpan.FromMinutes(expectedMinutes), TimeSpan.FromSeconds(1));

    /// <summary>
    /// The property the burst-breaking depends on: the same inputs must NOT always produce the same
    /// delay. Asserted over the jitter samples the callers actually feed it (Random.Shared).
    /// </summary>
    [Fact]
    public void NextRetryDelay_OverManySamples_SpreadsAcrossTheWholeJitterBand()
    {
        var options = Standard();
        var delays = Enumerable.Range(0, 500)
            .Select(_ => options.NextRetryDelay(1, null, Random.Shared.NextDouble()))
            .ToList();

        delays.Distinct().Should().HaveCountGreaterThan(100,
            "a fixed schedule re-fires a whole failed batch in one burst; the spread is the fix");
        delays.Min().Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30),
            "never earlier than the documented step");
        delays.Max().Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(36),
            "and never more than the declared jitter band above it");
    }

    /// <summary>
    /// A supplier can slow us down and cannot speed us up. A <c>Retry-After</c> SHORTER than our own
    /// step must not pull a rate-limited batch back in sooner than the backoff intended — which is
    /// the opposite of what the header is asking for.
    /// </summary>
    [Fact]
    public void NextRetryDelay_RetryAfter_IsAFloorNeverACeiling()
    {
        var options = Standard();

        options.NextRetryDelay(1, TimeSpan.FromMinutes(90), jitterSample: 0.0)
            .Should().Be(TimeSpan.FromMinutes(90),
                "they asked for longer than our 30-minute step, so we wait as long as they asked");

        options.NextRetryDelay(1, TimeSpan.FromMinutes(5), jitterSample: 0.0)
            .Should().Be(TimeSpan.FromMinutes(30),
                "they asked for less than our step — honouring that would make us MORE aggressive " +
                "against an endpoint that has just told us to back off");
    }

    /// <summary>
    /// And jitter still applies ON TOP of a Retry-After — which is the case that matters most, since
    /// a rate limiter hands the SAME value to every order in the batch. Honouring it without jitter
    /// would re-synchronise the burst precisely.
    /// </summary>
    [Fact]
    public void NextRetryDelay_JittersAboveAnHonouredRetryAfter_SoTheBatchStillSpreads()
    {
        var delay = Standard().NextRetryDelay(1, TimeSpan.FromMinutes(90), jitterSample: 1.0);

        delay.Should().BeGreaterThan(TimeSpan.FromMinutes(90));
        delay.Should().BeCloseTo(TimeSpan.FromMinutes(108), TimeSpan.FromSeconds(1)); // 90 + 20%
    }

    /// <summary>
    /// The bound that keeps jitter from colliding with the stranded-delivery sweep. That sweep
    /// re-drives an aged <c>delivery_failed</c> order after 3h, and its own comment justifies the
    /// threshold by "well past the maximum retry backoff". Jitter and a clamped Retry-After both
    /// raise that maximum, so the claim has to be re-checked here rather than assumed to survive.
    /// </summary>
    [Fact]
    public void NextRetryDelay_WorstCase_StaysUnderTheStrandedSweepThreshold()
    {
        var worst = Standard().NextRetryDelay(
            failedAttempts: 99,                       // clamps to the last step, 120 min
            supplierRetryAfter: TimeSpan.FromDays(1), // already bounded by RetryAfterHeader
            jitterSample: 1.0);

        worst.Should().BeLessThan(TimeSpan.FromHours(3),
            "StrandedFailedDeliveryDetectionJob sweeps delivery_failed orders older than 3h; a " +
            "backoff that reached past it would be re-driven by the sweep instead of waited out, " +
            "which is exactly the interference the threshold was chosen to avoid");
    }

    [Fact]
    public void NextRetryDelay_JitterDisabled_IsExactlyTheScheduledStep()
        => new DeliveryReliabilityOptions { BackoffMinutes = new[] { 30, 60, 120 }, RetryJitterPercent = 0 }
            .NextRetryDelay(2, null, 1.0)
            .Should().Be(TimeSpan.FromMinutes(60));

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void NextRetryDelay_OutOfRangeJitterSample_StaysInsideTheBand(double sample)
    {
        var delay = Standard().NextRetryDelay(1, null, sample);

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(30));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(36));
    }
}
