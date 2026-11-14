using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Constants;

public class TrialWindowTests
{
    [Fact]
    public void ATrialLastsThirtyDays() =>
        PlanConstants.PilotDuration.Should().Be(TimeSpan.FromDays(30));

    [Theory]
    [InlineData(14, null, 720)]    // began under the 14-day rule, never extended
    [InlineData(null, null, 720)]  // no stored end at all
    [InlineData(14, 718, 720)]     // extended by hand to two hours short of day 30
    [InlineData(14, 1080, 1080)]   // extended by hand to day 45
    [InlineData(60, null, 1440)]   // a stored end later than day 30 is kept
    public void EffectiveTrialEnd_IsTheLatestOf_StartPlusThirtyDays_TheStoredEnd_AndTheOverride(
        int? storedEndDays, int? overrideHours, int expectedHours)
    {
        var start = new DateTime(2026, 10, 29, 11, 57, 0, DateTimeKind.Utc);

        var end = PlanConstants.GetEffectiveTrialEnd(
            start,
            storedEndDays is { } d ? start.AddDays(d) : null,
            overrideHours is { } h ? start.AddHours(h) : null);

        end.Should().Be(start.AddHours(expectedHours));
    }
}
