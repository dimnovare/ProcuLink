using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
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
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "1000");
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 999);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeFalse(
            "999 tokens used out of 1000 should still allow another call");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_ReturnsTrueAtLimit()
    {
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "1000");
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 1000);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue(
            "at-limit usage should block further calls (cap is at-or-over)");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_ReturnsTrueWhenOverLimit()
    {
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "1000");
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 1500);

        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task IncrementAsync_AddsToExistingCounter()
    {
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "100000");
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
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "1000");
        var orgId = Guid.NewGuid();

        await tracker.IncrementAsync(orgId, 0);
        await tracker.IncrementAsync(orgId, -5);

        var snapshot = await tracker.GetCurrentAsync(orgId);
        snapshot.TokensUsed.Should().Be(0);
    }

    [Fact]
    public async Task UsageIsIsolatedPerOrg()
    {
        await using var db = CreateFullDb();
        var tracker = CreateTracker(db, brake: "1000");
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
        await using var db = CreateFullDb();
        var orgId = Guid.NewGuid();
        var clockMay = new MutableClock(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));
        var trackerMay = CreateTracker(db, brake: "1000", clock: () => clockMay.Now);

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

    // ── The allowance follows the documents read (founder decision 17) ──────

    [Fact]
    public async Task The_allowance_counts_units_read_this_month_and_units_being_read()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Scale, AccountStatusConstants.Active);
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        FlowDocument Doc(int units, DateTime? readAt, string status = DocumentStatus.Sent, bool sample = false) => new()
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = Guid.NewGuid(), FileName = "f.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", BillingUnits = units, ReadAt = readAt, Status = status, IsSample = sample,
            CreatedAt = now.UtcDateTime, UpdatedAt = now.UtcDateTime,
        };
        db.FlowDocuments.AddRange(
            Doc(30, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)),
            Doc(10, new DateTime(2026, 10, 31, 23, 59, 0, DateTimeKind.Utc)),            // last month
            Doc(5, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), sample: true),  // a sample
            Doc(4, null, DocumentStatus.Reading),                                        // being read now
            Doc(7, null, DocumentStatus.ReadFailed));                                    // never read
        await db.SaveChangesAsync();

        (await CreateTracker(db, clock: () => now).GetCurrentAsync(orgId)).TokensLimit.Should().Be(1_360_000);
    }

    [Fact]
    public async Task The_stale_flat_setting_is_ignored_and_only_the_brake_caps()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Scale, AccountStatusConstants.Active);
        var stale = CreateTracker(db, staleOverride: "100000");
        await stale.IncrementAsync(orgId, 150_000);

        (await stale.GetCurrentAsync(orgId)).TokensLimit.Should().Be(1_000_000);
        (await stale.IsAtOrOverLimitAsync(orgId)).Should().BeFalse();
        (await CreateTracker(db, brake: "500").GetCurrentAsync(orgId)).TokensLimit.Should().Be(500);
        (await CreateTracker(db, brake: "500").IsAtOrOverLimitAsync(orgId)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task Without_a_usable_brake_setting_the_250_million_default_caps_the_allowance(string? brake)
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Scale, AccountStatusConstants.Active);
        db.FlowDocuments.Add(new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = orgId, FlowId = Guid.NewGuid(), FileName = "f.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", BillingUnits = 10_000, ReadAt = DateTime.UtcNow, Status = DocumentStatus.Sent,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        (await CreateTracker(db, brake: brake).GetCurrentAsync(orgId)).TokensLimit.Should().Be(250_000_000);
    }

    // ── Account-status clamp on the AI budget (audit 2026-07-10 P2) ─────────
    // A read-only / delinquent org must not keep spending real OpenAI tokens at
    // its old allowance. Founder decision (2026-07-10): past_due keeps a small
    // GRACE budget through Stripe dunning (recoverable); read_only,
    // cancelled and trial_expired get ZERO. Good-standing orgs are untouched.

    [Fact]
    public async Task Snapshot_PastDuePaidOrg_ClampedToPilotGraceBudget()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.PastDue);
        var tracker = CreateTracker(db);

        (await tracker.GetCurrentAsync(orgId)).TokensLimit.Should().Be(200_000,
            "a past_due paid org keeps only the 200k grace budget during dunning, not its full allowance");
    }

    [Theory]
    [InlineData(AccountStatusConstants.ReadOnly)]
    [InlineData(AccountStatusConstants.Cancelled)]
    [InlineData(AccountStatusConstants.TrialExpired)]
    public async Task Snapshot_NonRecoverableReadOnlyStatus_ClampedToZero(string status)
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Operations, status);
        var tracker = CreateTracker(db);

        (await tracker.GetCurrentAsync(orgId)).TokensLimit.Should().Be(0,
            $"a {status} org must not spend any AI budget");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_CancelledOrg_BlocksAfterAnyUsage()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Growth, AccountStatusConstants.Cancelled);
        var tracker = CreateTracker(db);

        await tracker.IncrementAsync(orgId, 1);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue(
            "a cancelled org has a zero budget, so any recorded usage is already at/over the cap");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_PastDuePaidOrg_BlocksAtPilotGraceBudget()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Integration, AccountStatusConstants.PastDue);
        var tracker = CreateTracker(db);

        await tracker.IncrementAsync(orgId, 199_999);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeFalse(
            "just under the 200k grace budget is still allowed");

        await tracker.IncrementAsync(orgId, 1);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue(
            "at the 200k grace budget a past_due org is blocked, not at its full allowance");
    }

    // ── The allowance rule: pure policy function ────────────────────────────

    [Theory]
    [InlineData(0, AccountStatusConstants.Active, 1_000_000)]
    [InlineData(25, AccountStatusConstants.Active, 1_000_000)]
    [InlineData(26, AccountStatusConstants.Active, 1_040_000)]
    [InlineData(150, AccountStatusConstants.Trialing, 6_000_000)]
    [InlineData(5_000, AccountStatusConstants.Active, 200_000_000)]
    [InlineData(-3, AccountStatusConstants.Active, 1_000_000)]
    [InlineData(40, null, 1_600_000)]
    [InlineData(5_000, AccountStatusConstants.PastDue, 200_000)]
    [InlineData(5_000, AccountStatusConstants.ReadOnly, 0)]
    [InlineData(5_000, AccountStatusConstants.Cancelled, 0)]
    [InlineData(5_000, AccountStatusConstants.TrialExpired, 0)]
    public void The_allowance_follows_the_units_billed_and_the_account_status(long units, string? status, long expected) =>
        PlanConstants.GetAiMonthlyTokenAllowance(units, status).Should().Be(expected);

    // ── AccountStatusConstants.IsReadOnly: shared read-only status set ───────

    [Theory]
    [InlineData(AccountStatusConstants.TrialExpired)]
    [InlineData(AccountStatusConstants.PastDue)]
    [InlineData(AccountStatusConstants.ReadOnly)]
    [InlineData(AccountStatusConstants.Cancelled)]
    public void IsReadOnly_TrueForDelinquentStatuses(string status)
    {
        AccountStatusConstants.IsReadOnly(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(AccountStatusConstants.Active)]
    [InlineData(AccountStatusConstants.Trialing)]
    [InlineData("some-unknown-status")]
    [InlineData("")]
    [InlineData(null)]
    public void IsReadOnly_FalseForGoodStandingOrUnknown(string? status)
    {
        AccountStatusConstants.IsReadOnly(status).Should().BeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AiUsageTracker CreateTracker(
        ProcuLinkDbContext db,
        string? brake = null,
        Func<DateTimeOffset>? clock = null,
        string? staleOverride = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:OpenAI:EmergencyMonthlyTokenBrake"] = brake,
                ["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = staleOverride,
            })
            .Build();
        return new AiUsageTracker(db, config, clock ?? (() => DateTimeOffset.UtcNow));
    }

    private static ProcuLinkDbContext CreateFullDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedOrgAsync(
        ProcuLinkDbContext db,
        string plan,
        string accountStatus = AccountStatusConstants.Trialing)
    {
        var org = new Organisation
        {
            Id            = Guid.NewGuid(),
            ClerkOrgId    = $"org_{Guid.NewGuid():N}",
            Name          = "AI Cap Test Org",
            Slug          = $"ai-cap-{Guid.NewGuid():N}",
            Plan          = plan,
            AccountStatus = accountStatus,
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset start) { Now = start; }
        public DateTimeOffset Now { get; set; }
    }
}
