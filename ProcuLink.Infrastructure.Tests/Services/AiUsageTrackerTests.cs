using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests the Gap 2 per-org monthly cost cap: cap blocks calls when exceeded,
/// allows when under, and the counter increments correctly.
/// </summary>
public class AiUsageTrackerTests
{
    [Fact]
    public async Task IsAtOrOverLimitAsync_ReturnsFalseWhenUnderLimit()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 1000);
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 999);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeFalse(
            "999 tokens used out of 1000 should still allow another call");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_ReturnsTrueAtLimit()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 1000);
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 1000);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue(
            "at-limit usage should block further calls (cap is at-or-over)");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_ReturnsTrueWhenOverLimit()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 1000);
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 1500);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task IncrementAsync_AddsToExistingCounter()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 100_000);
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 100);
        await tracker.IncrementAsync(orgId, 250);
        await tracker.IncrementAsync(orgId, 75);

        var snapshot = await tracker.GetCurrentAsync(orgId);
        snapshot.TokensUsed.Should().Be(425);
        snapshot.TokensLimit.Should().Be(100_000);
    }

    [Fact]
    public async Task IncrementAsync_IsNoOpForNonPositive()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 1000);
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 0);
        await tracker.IncrementAsync(orgId, -5);

        var snapshot = await tracker.GetCurrentAsync(orgId);
        snapshot.TokensUsed.Should().Be(0);
    }

    [Fact]
    public async Task UsageIsIsolatedPerOrg()
    {
        await using var db = CreateDb();
        var tracker = CreateTracker(db, monthlyLimit: 1000);
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await tracker.IncrementAsync(orgA, 500);
        await tracker.IncrementAsync(orgB, 200);

        (await tracker.GetCurrentAsync(orgA)).TokensUsed.Should().Be(500);
        (await tracker.GetCurrentAsync(orgB)).TokensUsed.Should().Be(200);
    }

    [Fact]
    public async Task UsageIsIsolatedAcrossCalendarMonths()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var clockMay = new MutableClock(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));
        var trackerMay = CreateTracker(db, monthlyLimit: 1000, clock: () => clockMay.Now);

        await trackerMay.IncrementAsync(orgId, 800);
        (await trackerMay.IsAtOrOverLimitAsync(orgId)).Should().BeFalse();

        // Flip the clock to June; the new month gets its own row.
        clockMay.Now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        (await trackerMay.IsAtOrOverLimitAsync(orgId)).Should().BeFalse(
            "a new calendar month must reset the cap regardless of last month's usage");

        await trackerMay.IncrementAsync(orgId, 500);
        var june = await trackerMay.GetCurrentAsync(orgId);
        june.Month.Should().Be(6);
        june.TokensUsed.Should().Be(500);
    }

    [Fact]
    public void MonthlyLimit_FallsBackToDefaultWhenMissing()
    {
        var config = new ConfigurationBuilder().Build();
        var tracker = new AiUsageTracker(NoopDb(), config);

        tracker.MonthlyLimit.Should().Be(AiUsageTracker.DefaultMonthlyTokenLimit);
    }

    [Fact]
    public void MonthlyLimit_ReadsConfigValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = "42"
            })
            .Build();
        var tracker = new AiUsageTracker(NoopDb(), config);

        tracker.MonthlyLimit.Should().Be(42);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AiUsageTracker CreateTracker(
        ProcuLinkDbContext db,
        long monthlyLimit,
        Func<DateTimeOffset>? clock = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = monthlyLimit.ToString()
            })
            .Build();
        return new AiUsageTracker(db, config, clock ?? (() => DateTimeOffset.UtcNow));
    }

    private static ProcuLinkDbContext NoopDb() =>
        new AiUsageTestDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProcuLinkDbContext CreateDb() =>
        new AiUsageTestDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset start) { Now = start; }
        public DateTimeOffset Now { get; set; }
    }

    private sealed class AiUsageTestDbContext : ProcuLinkDbContext
    {
        public AiUsageTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();

            modelBuilder.Entity<AiUsageMonthly>(b =>
            {
                b.HasKey(x => new { x.OrgId, x.Year, x.Month });
            });
        }
    }
}
