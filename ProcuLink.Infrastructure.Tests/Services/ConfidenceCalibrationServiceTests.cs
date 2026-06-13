using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// V9 confidence calibration: pins the bucketing + Beta(1,1) smoothing math, the min-sample and
/// org-floor honesty guards, the calibrate-toward-empirical-rate behaviour for over-/under-confident
/// orgs, cold-start raw passthrough, strict org/tenant isolation, and the exclusion of <c>manual</c>
/// and null-confidence rows. The decision table is the same one the live resolve/accept path writes.
/// </summary>
public class ConfidenceCalibrationServiceTests
{
    // ── Bucket boundary + smoothing primitives (pure, no DB) ──────────────────

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.69, 1)]
    [InlineData(0.7, 2)]
    [InlineData(0.84, 2)]
    [InlineData(0.85, 3)]
    [InlineData(0.94, 3)]
    [InlineData(0.95, 4)]
    [InlineData(1.0, 4)]            // top bucket is inclusive of 1.0
    [InlineData(1.5, 4)]           // clamped above 1.0
    [InlineData(-0.2, 0)]          // clamped below 0.0
    public void BucketIndexFor_AssignsTheDocumentedBuckets(double raw, int expectedIdx)
    {
        ConfidenceCalibration.BucketIndexFor(raw).Should().Be(expectedIdx);
    }

    [Fact]
    public void SmoothedAcceptRate_IsBeta11Laplace()
    {
        // (accepted+1)/(total+2). Zero data → 0.5 (uniform prior mean).
        ConfidenceCalibration.SmoothedAcceptRate(0, 0).Should().Be(0.5);
        // 8/8 accepted: naive 1.0, smoothed (8+1)/(8+2) = 0.9 — pulled off the hard edge.
        ConfidenceCalibration.SmoothedAcceptRate(8, 8).Should().BeApproximately(0.9, 1e-9);
        // 0/8 accepted: naive 0.0, smoothed (0+1)/(8+2) = 0.1.
        ConfidenceCalibration.SmoothedAcceptRate(0, 8).Should().BeApproximately(0.1, 1e-9);
        // 4/8: balanced → exactly 0.5.
        ConfidenceCalibration.SmoothedAcceptRate(4, 8).Should().BeApproximately(0.5, 1e-9);
    }

    // ── Cold start: no history → raw passthrough, uncalibrated ────────────────

    [Fact]
    public async Task Calibrate_NoHistory_PassesRawThrough_Uncalibrated()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var result = await svc.CalibrateAsync(Guid.NewGuid(), 0.91);

        result.IsCalibrated.Should().BeFalse();
        result.RawConfidence.Should().Be(0.91);
        result.CalibratedConfidence.Should().Be(0.91, "with no history the raw value must pass through unchanged");
        result.SampleSize.Should().Be(0);
        result.Basis.Should().Be(ConfidenceCalibration.UncalibratedBasis);
    }

    [Fact]
    public async Task Calibrate_BelowOrgFloor_PassesRawThrough_Uncalibrated()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        // Fewer than MinOrgSamples (8) decisions overall → calibration OFF entirely.
        await SeedDecisionsAsync(db, orgId, 0.9, accepted: 3, rejected: 2); // 5 < 8
        var svc = NewService(db);

        var result = await svc.CalibrateAsync(orgId, 0.9);

        result.IsCalibrated.Should().BeFalse("the org is below the total-decision floor");
        result.CalibratedConfidence.Should().Be(0.9);
        result.SampleSize.Should().Be(0);
    }

    // ── Min-sample guard: org has enough total, but the *bucket* is thin ──────

    [Fact]
    public async Task Calibrate_OrgAboveFloor_ButThinBucket_FallsBackToRaw_NotASwungValue()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        // 10 decisions in the [0.5,0.7) bucket → org clears the floor and that bucket is trusted.
        await SeedDecisionsAsync(db, orgId, 0.6, accepted: 5, rejected: 5);
        // Only 3 decisions in the [0.95,1.0] bucket → that bucket is NOT trusted (3 < 8).
        await SeedDecisionsAsync(db, orgId, 0.97, accepted: 3, rejected: 0);

        var svc = NewService(db);

        // A raw value landing in the thin top bucket must pass through as RAW — never swing to the
        // 3/3 → smoothed (3+1)/(3+2)=0.8 value we can't back with enough samples.
        var result = await svc.CalibrateAsync(orgId, 0.97);

        result.IsCalibrated.Should().BeFalse("the [0.95,1.0] bucket has only 3 samples (< 8)");
        result.CalibratedConfidence.Should().Be(0.97, "a thin bucket must not show a calibrated number");
        result.SampleSize.Should().Be(0);
    }

    // ── Over-confident org: raw 0.9 historically accepted ~half the time → pull DOWN ──

    [Fact]
    public async Task Calibrate_OverconfidentOrg_PullsHighRawDownTowardEmpiricalRate()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        // In the [0.85,0.95) bucket the model said ~0.9 but was accepted only half the time:
        // 10 accepted / 10 rejected = 20 samples (≥ 8). Smoothed = (10+1)/(20+2) = 0.5.
        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 10, rejected: 10);
        var svc = NewService(db);

        var result = await svc.CalibrateAsync(orgId, 0.90);

        result.IsCalibrated.Should().BeTrue();
        result.RawConfidence.Should().Be(0.90);
        result.CalibratedConfidence.Should().BeApproximately(0.5, 1e-9,
            "an over-confident 0.9 bucket that's accepted half the time must be pulled toward 0.5");
        result.CalibratedConfidence.Should().BeLessThan(result.RawConfidence);
        result.SampleSize.Should().Be(20);
        result.Basis.Should().Be(ConfidenceCalibration.CalibratedBasis(20));
    }

    // ── Under-confident org: raw 0.6 historically accepted almost always → pull UP ──

    [Fact]
    public async Task Calibrate_UnderconfidentOrg_PullsLowRawUpTowardEmpiricalRate()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        // [0.5,0.7) bucket: a cautious model said ~0.6 but was almost always right:
        // 18 accepted / 2 rejected = 20 (≥ 8). Smoothed = (18+1)/(20+2) = 0.8636…
        await SeedDecisionsAsync(db, orgId, 0.60, accepted: 18, rejected: 2);
        var svc = NewService(db);

        var result = await svc.CalibrateAsync(orgId, 0.60);

        result.IsCalibrated.Should().BeTrue();
        result.CalibratedConfidence.Should().BeApproximately(19.0 / 22.0, 1e-9);
        result.CalibratedConfidence.Should().BeGreaterThan(result.RawConfidence,
            "an under-confident 0.6 bucket that's almost always accepted must be pulled UP");
        result.SampleSize.Should().Be(20);
    }

    // ── Tenant isolation: org B's decisions never affect org A's curve ────────

    [Fact]
    public async Task Calibrate_IsOrgScoped_OrgBDecisionsNeverAffectOrgA()
    {
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        // Org B has a rich, over-confident [0.85,0.95) bucket.
        await SeedDecisionsAsync(db, orgB, 0.90, accepted: 10, rejected: 10);
        // Org A has NOTHING.
        var svc = NewService(db);

        var aResult = await svc.CalibrateAsync(orgA, 0.90);
        aResult.IsCalibrated.Should().BeFalse("org A has no history of its own");
        aResult.CalibratedConfidence.Should().Be(0.90, "org B's curve must never leak into org A");

        // And org B still calibrates from its own data.
        var bResult = await svc.CalibrateAsync(orgB, 0.90);
        bResult.IsCalibrated.Should().BeTrue();
        bResult.CalibratedConfidence.Should().BeApproximately(0.5, 1e-9);
    }

    // ── Exclusions: manual rows + null-confidence rows must not corrupt the curve ──

    [Fact]
    public async Task Calibrate_ExcludesManualAndNullConfidenceRows()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        // 12 real verdicts in [0.85,0.95): 6 accepted / 6 rejected → smoothed (6+1)/(12+2)=0.5.
        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 6, rejected: 6);

        // Noise that MUST be ignored: a pile of manual + null-confidence rows that, if counted,
        // would skew the bucket. Manual rows carry an empty suggested code; null-confidence rows
        // would otherwise pretend to be high-confidence accepts.
        var rows = new List<AiSuggestionDecision>();
        for (var i = 0; i < 20; i++)
            rows.Add(MakeDecision(orgId, lineNumber: 1000 + i,
                confidence: null, decision: AiSuggestionDecisionKind.Manual,
                suggested: "", chosen: "SUP-X"));
        for (var i = 0; i < 20; i++)
            rows.Add(MakeDecision(orgId, lineNumber: 2000 + i,
                confidence: null, decision: AiSuggestionDecisionKind.Accepted,
                suggested: "SUP-Y", chosen: "SUP-Y"));
        db.AiSuggestionDecisions.AddRange(rows);
        await db.SaveChangesAsync();

        var svc = NewService(db);

        var result = await svc.CalibrateAsync(orgId, 0.90);
        result.IsCalibrated.Should().BeTrue();
        result.SampleSize.Should().Be(12, "only the 12 real, scored verdicts count");
        result.CalibratedConfidence.Should().BeApproximately(0.5, 1e-9);

        var summary = await svc.GetCalibrationSummaryAsync(orgId);
        summary.TotalDecisions.Should().Be(12, "manual + null-confidence rows are excluded from the org total");
    }

    // ── Calibration never invents confidence for a suggestion that had none ───
    // (Covered structurally: the service only ever takes a raw double in; null-confidence rows
    //  never enter the curve, so a line with no AI confidence is never given one. The DTO wiring
    //  test in the Api.Tests project asserts the null-suggestion → no-calibration end of this.)

    // ── Summary view: buckets, counts, accept rates, active flag ──────────────

    [Fact]
    public async Task GetSummary_ReportsBuckets_Counts_Rates_AndActiveFlag()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 10, rejected: 10); // bucket 3, trusted
        await SeedDecisionsAsync(db, orgId, 0.60, accepted: 1, rejected: 1);   // bucket 1, thin (2)
        var svc = NewService(db);

        var summary = await svc.GetCalibrationSummaryAsync(orgId);

        summary.Buckets.Should().HaveCount(ConfidenceCalibration.BucketLowerBounds.Count);
        summary.TotalDecisions.Should().Be(22);
        summary.IsActive.Should().BeTrue("there is a trusted bucket and the org cleared the floor");

        var b3 = summary.Buckets[3];
        b3.LowerInclusive.Should().Be(0.85);
        b3.UpperExclusive.Should().Be(0.95);
        b3.Accepted.Should().Be(10);
        b3.Rejected.Should().Be(10);
        b3.Total.Should().Be(20);
        b3.IsTrusted.Should().BeTrue();
        b3.SmoothedAcceptRate.Should().BeApproximately(0.5, 1e-9);

        var b1 = summary.Buckets[1];
        b1.Total.Should().Be(2);
        b1.IsTrusted.Should().BeFalse("2 samples < the 8 min");
    }

    [Fact]
    public async Task GetSummary_OrgWithOnlyThinBuckets_IsNotActive()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        // 10 total decisions (clears the floor) but spread so NO single bucket reaches 8.
        await SeedDecisionsAsync(db, orgId, 0.60, accepted: 3, rejected: 2); // bucket 1: 5
        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 3, rejected: 2); // bucket 3: 5
        var svc = NewService(db);

        var summary = await svc.GetCalibrationSummaryAsync(orgId);

        summary.TotalDecisions.Should().Be(10);
        summary.IsActive.Should().BeFalse("no individual bucket has enough samples to be trusted");
        summary.Buckets.All(b => !b.IsTrusted).Should().BeTrue();
    }

    // ── Caching: the curve is cached per-org for the TTL window ───────────────

    [Fact]
    public async Task Curve_IsCachedPerOrg_NewDecisionsNotVisibleUntilTtl()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 10, rejected: 10);

        // Share ONE cache across two service instances to prove the cache, not the instance, holds it.
        var cache = NewCache();
        var svc1 = new ConfidenceCalibrationService(db, cache);

        var first = await svc1.CalibrateAsync(orgId, 0.90);
        first.SampleSize.Should().Be(20);

        // Add a fresh batch directly to the DB; the cached curve should NOT yet reflect it.
        await SeedDecisionsAsync(db, orgId, 0.90, accepted: 10, rejected: 10);

        var svc2 = new ConfidenceCalibrationService(db, cache);
        var second = await svc2.CalibrateAsync(orgId, 0.90);
        second.SampleSize.Should().Be(20, "the per-org curve is cached for the TTL window; new rows wait for expiry");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // Sizeless cache to mirror the default DI MemoryCache (AddMemoryCache has no SizeLimit, and the
    // service deliberately sets no per-entry Size).
    private static IMemoryCache NewCache() =>
        new MemoryCache(new MemoryCacheOptions());

    private static ConfidenceCalibrationService NewService(ProcuLinkDbContext db) =>
        new(db, NewCache());

    /// <summary>
    /// Seeds <paramref name="accepted"/> accepted + <paramref name="rejected"/> rejected scored
    /// verdicts all at the given raw <paramref name="confidence"/> (so they all land in one bucket).
    /// Line numbers are made unique per row so the (org,order,line,code,decision) shape is distinct.
    /// </summary>
    private static async Task SeedDecisionsAsync(
        ProcuLinkDbContext db, Guid orgId, double confidence, int accepted, int rejected)
    {
        var rows = new List<AiSuggestionDecision>();
        var baseline = Math.Abs((confidence, accepted, rejected, Guid.NewGuid()).GetHashCode());

        for (var i = 0; i < accepted; i++)
            rows.Add(MakeDecision(orgId, lineNumber: baseline + i,
                confidence: confidence, decision: AiSuggestionDecisionKind.Accepted,
                suggested: "SUP-A", chosen: "SUP-A"));

        for (var i = 0; i < rejected; i++)
            rows.Add(MakeDecision(orgId, lineNumber: baseline + 100_000 + i,
                confidence: confidence, decision: AiSuggestionDecisionKind.Rejected,
                suggested: "SUP-A", chosen: "SUP-B"));

        db.AiSuggestionDecisions.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AiSuggestionDecision MakeDecision(
        Guid orgId, int lineNumber, double? confidence, string decision, string suggested, string? chosen) => new()
    {
        Id                        = Guid.NewGuid(),
        OrgId                     = orgId,
        OrderId                   = Guid.NewGuid(),
        LineNumber                = lineNumber,
        SuggestedSupplierItemCode = suggested,
        ChosenSupplierItemCode    = chosen,
        Confidence                = confidence,
        ModelVersion              = "gpt-5-mini",
        Decision                  = decision,
        DecidedBy                 = "user",
        DecidedAt                 = DateTime.UtcNow,
    };
}
