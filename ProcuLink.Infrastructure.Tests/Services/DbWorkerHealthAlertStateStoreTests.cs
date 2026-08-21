using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The production cooldown store. <c>WorkerHealthAlertServiceTests</c> proves the sweep's decision
/// logic against a fake store; these prove the real one actually writes and reads back, because a
/// fake that both sides of a test agree on can round-trip perfectly while the shipped store does
/// nothing.
/// <para>
/// The store is a SINGLETON that opens its own <c>IServiceScope</c> per call — the state object it
/// backs is a singleton and must never capture a scoped <c>DbContext</c> — so the tests resolve it
/// through a real container rather than handing it a context directly.
/// </para>
/// </summary>
public class DbWorkerHealthAlertStateStoreTests
{
    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryFieldOfACondition()
    {
        using var provider = BuildProvider();
        var store = new DbWorkerHealthAlertStateStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        var alertedAt = new DateTime(2026, 8, 20, 13, 45, 0, DateTimeKind.Utc);
        await store.SaveAsync(
            new[] { new WorkerHealthAlertConditionState(
                OperationalAlertKeys.WorkerHeartbeatLost, WasBad: true, LastAlertUtc: alertedAt) },
            default);

        var loaded = await store.LoadAsync(default);

        loaded.Should().ContainSingle();
        loaded[0].AlertKey.Should().Be(OperationalAlertKeys.WorkerHeartbeatLost);
        loaded[0].WasBad.Should().BeTrue();
        loaded[0].LastAlertUtc.Should().Be(alertedAt);
    }

    [Fact]
    public async Task SaveAsync_UpsertsRatherThanDuplicating()
    {
        using var provider = BuildProvider();
        var store = new DbWorkerHealthAlertStateStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        var first = new DateTime(2026, 8, 20, 13, 45, 0, DateTimeKind.Utc);
        var second = first.AddMinutes(30);

        await store.SaveAsync(
            new[] { new WorkerHealthAlertConditionState(
                OperationalAlertKeys.DeadLetterBacklog, true, first) }, default);
        await store.SaveAsync(
            new[] { new WorkerHealthAlertConditionState(
                OperationalAlertKeys.DeadLetterBacklog, true, second) }, default);

        var loaded = await store.LoadAsync(default);

        loaded.Should().ContainSingle("the alert key is the primary key — one row per condition");
        loaded[0].LastAlertUtc.Should().Be(second);
    }

    [Fact]
    public async Task LoadAsync_OnAnEmptyStore_IsEmptyRatherThanAThrow()
    {
        using var provider = BuildProvider();
        var store = new DbWorkerHealthAlertStateStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        (await store.LoadAsync(default)).Should().BeEmpty(
            "no rows yet is a real answer — every condition is freshly armed on a brand-new "
          + "deployment, which is different from the store being unreadable");
    }

    [Fact]
    public async Task SaveAsync_KeepsConditionsIndependent()
    {
        using var provider = BuildProvider();
        var store = new DbWorkerHealthAlertStateStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        var at = new DateTime(2026, 8, 20, 13, 45, 0, DateTimeKind.Utc);
        await store.SaveAsync(
            new[]
            {
                new WorkerHealthAlertConditionState(OperationalAlertKeys.WorkerHeartbeatLost, true, at),
                new WorkerHealthAlertConditionState(OperationalAlertKeys.AiTokenCapLatched, false, null),
            },
            default);

        var loaded = (await store.LoadAsync(default)).ToDictionary(s => s.AlertKey);

        loaded[OperationalAlertKeys.WorkerHeartbeatLost].WasBad.Should().BeTrue();
        loaded[OperationalAlertKeys.AiTokenCapLatched].WasBad.Should().BeFalse();
        loaded[OperationalAlertKeys.AiTokenCapLatched].LastAlertUtc.Should().BeNull(
            "null means never alerted — a sentinel timestamp would be a fabricated instant");
    }

    /// <summary>
    /// The end-to-end shape of the fix: the same durable store behind two different
    /// <see cref="WorkerHealthAlertState"/> instances, which is what a Worker restart looks like.
    /// </summary>
    [Fact]
    public async Task StateOverTheDbStore_KeepsTheCooldownAcrossANewStateInstance()
    {
        using var provider = BuildProvider();
        var store = new DbWorkerHealthAlertStateStore(
            provider.GetRequiredService<IServiceScopeFactory>());

        var now = new DateTime(2026, 8, 20, 14, 50, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(30);

        var before = new WorkerHealthAlertState(store);
        (await before.BeginSweepAsync(default)).Should().BeNull();
        before.ShouldAlert(OperationalAlertKeys.WorkerHeartbeatLost, true, now, window)
              .Should().BeTrue();
        (await before.CommitSweepAsync(now, default)).Should().BeNull();

        // Restart.
        var after = new WorkerHealthAlertState(store);
        (await after.BeginSweepAsync(default)).Should().BeNull();
        after.ShouldAlert(OperationalAlertKeys.WorkerHeartbeatLost, true, now.AddMinutes(5), window)
             .Should().BeFalse("the persisted cooldown has not elapsed");

        // …and it is a cooldown, not a gag.
        after.ShouldAlert(OperationalAlertKeys.WorkerHeartbeatLost, true, now.AddMinutes(31), window)
             .Should().BeTrue("once the window genuinely elapses the condition must page again");
    }

    private static ServiceProvider BuildProvider()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }
}
