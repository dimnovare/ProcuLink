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
}
