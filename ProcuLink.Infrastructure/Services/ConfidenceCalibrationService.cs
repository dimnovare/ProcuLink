using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// V9 — EF Core implementation of <see cref="IConfidenceCalibrationService"/>. Builds a per-org
/// empirical accept-rate curve from the durable <c>AiSuggestionDecision</c> history and uses it
/// to translate a raw model confidence into a calibrated, evidence-backed one.
///
/// <para><b>What counts as evidence:</b> only rows that represent a real AI-suggestion verdict —
/// <c>Decision ∈ {accepted, rejected}</c> with a non-null <c>Confidence</c> AND a non-empty
/// suggested code. <c>manual</c> rows (no AI suggestion to judge) and null-confidence rows are
/// excluded; including them would let "the human typed a code by hand" masquerade as "the model
/// was right", corrupting the curve.</para>
///
/// <para><b>Tenancy:</b> every query is filtered by <c>d.OrgId == orgId</c>. One org's decisions
/// can never enter another org's curve, and the cache key is the org id.</para>
///
/// <para><b>Caching:</b> the curve changes slowly (it only moves as operators resolve more
/// orders), so it is cached per-org in <see cref="IMemoryCache"/> for
/// <see cref="CacheTtl"/> (10 minutes). Invalidation is purely by TTL — a freshly recorded
/// decision shows up in the curve within ≤10 min, which is fine for a display/insight layer.
/// Recompute is a single grouped aggregate over a small per-org table, so a cache miss is cheap.</para>
///
/// <para><b>Honesty:</b> a calibrated number is returned ONLY when the bucket the raw value
/// lands in has ≥ <see cref="ConfidenceCalibration.MinBucketSamples"/> verdicts AND the org has
/// ≥ <see cref="ConfidenceCalibration.MinOrgSamples"/> total verdicts. Otherwise the raw value
/// passes through unchanged with <c>IsCalibrated = false</c>. This is a derived layer — it never
/// mutates the stored raw <c>Confidence</c> and never invents confidence where there was none.</para>
/// </summary>
public sealed class ConfidenceCalibrationService : IConfidenceCalibrationService
{
    /// <summary>How long a computed per-org curve stays cached before recompute.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private const string CacheKeyPrefix = "confidence-calibration:";

    private readonly ProcuLinkDbContext _db;
    private readonly IMemoryCache _cache;

    public ConfidenceCalibrationService(ProcuLinkDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    /// <inheritdoc/>
    public async Task<CalibrationResult> CalibrateAsync(
        Guid orgId, double rawConfidence, CancellationToken ct = default)
    {
        var curve = await GetCurveAsync(orgId, ct);
        return curve.Calibrate(rawConfidence);
    }

    /// <inheritdoc/>
    public async Task<CalibrationSummary> GetCalibrationSummaryAsync(
        Guid orgId, CancellationToken ct = default)
    {
        var curve = await GetCurveAsync(orgId, ct);
        return curve.ToSummary();
    }

    /// <summary>
    /// Returns the cached per-org curve, computing it from the decision history on a miss.
    /// The cache entry is keyed by org id and expires after <see cref="CacheTtl"/>.
    /// </summary>
    private async Task<CalibrationCurve> GetCurveAsync(Guid orgId, CancellationToken ct)
    {
        var key = CacheKeyPrefix + orgId;
        if (_cache.TryGetValue(key, out CalibrationCurve? cached) && cached is not null)
            return cached;

        var curve = await ComputeCurveAsync(orgId, ct);

        // Note: deliberately NO entry Size — the default DI MemoryCache (AddMemoryCache) has no
        // SizeLimit, and setting an entry Size on a sizeless cache throws. The curve is tiny (a
        // few ints per org) and TTL-bounded, so an explicit size cap isn't needed.
        _cache.Set(key, curve, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
        });

        return curve;
    }

    /// <summary>
    /// Computes the empirical accept-rate curve from this org's usable decision rows with a single
    /// grouped aggregate. Org-scoped. Excludes <c>manual</c> + null-confidence + empty-code rows.
    /// </summary>
    private async Task<CalibrationCurve> ComputeCurveAsync(Guid orgId, CancellationToken ct)
    {
        // Pull only the two fields the math needs, for usable verdicts only. The filter runs in
        // the database (org-scoped); the in-memory bucketing below is over a small projection.
        // (Decision is a free-form string column → compare to the canonical kind constants.)
        var verdicts = await _db.AiSuggestionDecisions
            .AsNoTracking()
            .Where(d => d.OrgId == orgId
                        && d.Confidence != null
                        && d.SuggestedSupplierItemCode != ""
                        && (d.Decision == AiSuggestionDecisionKind.Accepted
                            || d.Decision == AiSuggestionDecisionKind.Rejected))
            .Select(d => new { d.Confidence, d.Decision })
            .ToListAsync(ct);

        var bucketCount = ConfidenceCalibration.BucketLowerBounds.Count;
        var accepted = new int[bucketCount];
        var rejected = new int[bucketCount];

        foreach (var v in verdicts)
        {
            // Confidence is non-null here (filtered above); clamp defends against any stored
            // out-of-range value so it still lands in a real bucket.
            var idx = ConfidenceCalibration.BucketIndexFor(v.Confidence!.Value);
            if (v.Decision == AiSuggestionDecisionKind.Accepted)
                accepted[idx]++;
            else
                rejected[idx]++;
        }

        return new CalibrationCurve(accepted, rejected);
    }

    /// <summary>
    /// An immutable per-org calibration curve: per-bucket accepted/rejected counts plus the pure
    /// math that turns them into a <see cref="CalibrationResult"/> / <see cref="CalibrationSummary"/>.
    /// Kept as a nested type so the caching layer can hold one instance per org and the math is
    /// directly unit-testable without a DbContext or cache.
    /// </summary>
    internal sealed class CalibrationCurve
    {
        private readonly int[] _accepted;
        private readonly int[] _rejected;
        private readonly int _totalDecisions;

        public CalibrationCurve(int[] accepted, int[] rejected)
        {
            _accepted = accepted;
            _rejected = rejected;
            _totalDecisions = accepted.Sum() + rejected.Sum();
        }

        /// <summary>True when the org has enough total history for calibration to engage at all.</summary>
        private bool OrgHasFloor => _totalDecisions >= ConfidenceCalibration.MinOrgSamples;

        /// <summary>Calibrates one raw confidence value against this curve (pure).</summary>
        public CalibrationResult Calibrate(double rawConfidence)
        {
            var raw = Math.Clamp(rawConfidence, 0.0, 1.0);
            var idx = ConfidenceCalibration.BucketIndexFor(raw);
            var total = _accepted[idx] + _rejected[idx];

            // Honesty gates: the org must have enough total history AND this specific bucket must
            // have enough samples. Either failing → raw passthrough, explicitly uncalibrated.
            if (!OrgHasFloor || total < ConfidenceCalibration.MinBucketSamples)
            {
                return new CalibrationResult(
                    RawConfidence: raw,
                    CalibratedConfidence: raw,
                    IsCalibrated: false,
                    SampleSize: 0,
                    Basis: ConfidenceCalibration.UncalibratedBasis);
            }

            var calibrated = ConfidenceCalibration.SmoothedAcceptRate(_accepted[idx], total);
            return new CalibrationResult(
                RawConfidence: raw,
                CalibratedConfidence: calibrated,
                IsCalibrated: true,
                SampleSize: total,
                Basis: ConfidenceCalibration.CalibratedBasis(total));
        }

        /// <summary>Projects the curve into the per-org summary view (pure).</summary>
        public CalibrationSummary ToSummary()
        {
            var buckets = new List<CalibrationBucket>(_accepted.Length);
            var anyTrusted = false;

            for (var i = 0; i < _accepted.Length; i++)
            {
                var total = _accepted[i] + _rejected[i];
                var trusted = total >= ConfidenceCalibration.MinBucketSamples;
                anyTrusted |= trusted;

                buckets.Add(new CalibrationBucket(
                    LowerInclusive: ConfidenceCalibration.LowerBoundOf(i),
                    UpperExclusive: ConfidenceCalibration.UpperBoundOf(i),
                    Accepted: _accepted[i],
                    Rejected: _rejected[i],
                    SmoothedAcceptRate: ConfidenceCalibration.SmoothedAcceptRate(_accepted[i], total),
                    IsTrusted: trusted));
            }

            // Calibration is "active" only when the org cleared the floor AND at least one bucket
            // is trusted — i.e. at least some raw values would actually be calibrated.
            return new CalibrationSummary(
                IsActive: OrgHasFloor && anyTrusted,
                TotalDecisions: _totalDecisions,
                Buckets: buckets);
        }
    }
}
