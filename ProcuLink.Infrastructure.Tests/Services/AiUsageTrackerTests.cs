using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
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
    public async Task Snapshot_ReadsConfigOverrideValue_WithoutTouchingOrganisations()
    {
        // The test DbContext IGNORES the Organisation entity, so this also
        // proves the config override short-circuits BEFORE any org/plan query.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = "42"
            })
            .Build();
        await using var db = CreateDb();
        var tracker = new AiUsageTracker(db, config);

        var snapshot = await tracker.GetCurrentAsync(Guid.NewGuid());
        snapshot.TokensLimit.Should().Be(42);
    }

    // ── Plan-aware limits (config override absent) ──────────────────────────

    [Theory]
    [InlineData(PlanConstants.Pilot,       200_000)]
    [InlineData(PlanConstants.Growth,      1_000_000)]
    [InlineData(PlanConstants.Operations,  2_500_000)]
    [InlineData(PlanConstants.Integration, 5_000_000)]
    [InlineData(PlanConstants.Distributor, 5_000_000)]
    [InlineData(PlanConstants.Enterprise,  10_000_000)]
    public void GetAiMonthlyTokenLimit_MapsEveryPlanTier(string plan, long expected)
    {
        PlanConstants.GetAiMonthlyTokenLimit(plan).Should().Be(expected);
    }

    [Theory]
    [InlineData("some-legacy-plan")]
    [InlineData("")]
    [InlineData(null)]
    public void GetAiMonthlyTokenLimit_UnknownOrNullPlan_FallsBackToPilot(string? plan)
    {
        PlanConstants.GetAiMonthlyTokenLimit(plan)
            .Should().Be(PlanConstants.AiMonthlyTokenLimits[PlanConstants.Pilot],
                "an unrecognised plan must never get a bigger AI budget than the trial tier");
    }

    [Theory]
    [InlineData(PlanConstants.Pilot,       200_000)]
    [InlineData(PlanConstants.Growth,      1_000_000)]
    [InlineData(PlanConstants.Operations,  2_500_000)]
    [InlineData(PlanConstants.Integration, 5_000_000)]
    [InlineData(PlanConstants.Distributor, 5_000_000)]
    [InlineData(PlanConstants.Enterprise,  10_000_000)]
    public async Task Snapshot_ResolvesPlanDefault_WhenNoConfigOverride(string plan, long expected)
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, plan);
        var tracker = CreatePlanAwareTracker(db);

        var snapshot = await tracker.GetCurrentAsync(orgId);

        snapshot.TokensLimit.Should().Be(expected,
            "the ai-usage snapshot must report the org's actual resolved limit");
    }

    [Fact]
    public async Task Snapshot_UnknownPlan_FallsBackToPilotLimit()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, "mystery-plan");
        var tracker = CreatePlanAwareTracker(db);

        (await tracker.GetCurrentAsync(orgId)).TokensLimit.Should().Be(200_000);
    }

    [Fact]
    public async Task Snapshot_MissingOrg_FallsBackToPilotLimit()
    {
        await using var db = CreateFullDb();
        var tracker = CreatePlanAwareTracker(db);

        (await tracker.GetCurrentAsync(Guid.NewGuid())).TokensLimit.Should().Be(200_000,
            "an org row we cannot find must fail safe to the smallest (Pilot) budget");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_UsesPlanLimit_SameUsageBlocksLowerTierOnly()
    {
        // 1,000,000 tokens: AT the Growth limit (blocked) but UNDER Operations (allowed).
        await using var db = CreateFullDb();
        var growthOrg     = await SeedOrgAsync(db, PlanConstants.Growth);
        var operationsOrg = await SeedOrgAsync(db, PlanConstants.Operations);
        var tracker = CreatePlanAwareTracker(db);

        await tracker.IncrementAsync(growthOrg, 1_000_000);
        await tracker.IncrementAsync(operationsOrg, 1_000_000);

        (await tracker.IsAtOrOverLimitAsync(growthOrg)).Should().BeTrue(
            "1M tokens is at the Growth limit");
        (await tracker.IsAtOrOverLimitAsync(operationsOrg)).Should().BeFalse(
            "1M tokens is well under the 2.5M Operations limit");
    }

    [Fact]
    public async Task IsAtOrOverLimitAsync_PilotOrg_BlocksAtPilotLimit()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Pilot);
        var tracker = CreatePlanAwareTracker(db);

        await tracker.IncrementAsync(orgId, 199_999);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeFalse();

        await tracker.IncrementAsync(orgId, 1);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue();
    }

    [Fact]
    public async Task ConfigOverride_BeatsEveryPlan()
    {
        // Enterprise would get 10M by plan, but the explicit config override wins —
        // this is the production emergency lever and must keep current behaviour.
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Enterprise);
        var tracker = CreatePlanAwareTracker(db, configOverride: 500);

        (await tracker.GetCurrentAsync(orgId)).TokensLimit.Should().Be(500);

        await tracker.IncrementAsync(orgId, 500);
        (await tracker.IsAtOrOverLimitAsync(orgId)).Should().BeTrue(
            "the config override caps even an Enterprise org");
    }

    [Fact]
    public async Task ConfigOverride_NonPositiveOrUnparseable_FallsThroughToPlan()
    {
        await using var db = CreateFullDb();
        var orgId = await SeedOrgAsync(db, PlanConstants.Growth);

        foreach (var bad in new[] { "0", "-1", "not-a-number" })
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = bad
                })
                .Build();
            var tracker = new AiUsageTracker(db, config);

            (await tracker.GetCurrentAsync(orgId)).TokensLimit.Should().Be(1_000_000,
                $"override '{bad}' is invalid and must fall through to the Growth plan default");
        }
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

    private static ProcuLinkDbContext CreateDb() =>
        new AiUsageTestDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// Real model (Organisation included) for the plan-aware limit tests —
    /// the tracker resolves the limit from <c>organisations.plan</c>.
    /// </summary>
    private static ProcuLinkDbContext CreateFullDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AiUsageTracker CreatePlanAwareTracker(
        ProcuLinkDbContext db,
        long? configOverride = null,
        Func<DateTimeOffset>? clock = null)
    {
        var values = new Dictionary<string, string?>();
        if (configOverride is { } o)
            values["Ai:OpenAI:MonthlyTokenLimitPerOrg"] = o.ToString();

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AiUsageTracker(db, config, clock ?? (() => DateTimeOffset.UtcNow));
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string plan)
    {
        var org = new Organisation
        {
            Id         = Guid.NewGuid(),
            ClerkOrgId = $"org_{Guid.NewGuid():N}",
            Name       = "AI Cap Test Org",
            Slug       = $"ai-cap-{Guid.NewGuid():N}",
            Plan       = plan,
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
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<PoPassportEvent>();

            modelBuilder.Entity<AiUsageMonthly>(b =>
            {
                b.HasKey(x => new { x.OrgId, x.Year, x.Month });
            });
        }
    }
}
