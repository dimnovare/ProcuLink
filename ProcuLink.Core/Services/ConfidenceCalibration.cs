namespace ProcuLink.Core.Services;

/// <summary>
/// The tunable constants of the V9 confidence-calibration math, gathered in one place so the
/// rationale is documented and the tests pin the exact numbers.
///
/// <para><b>Buckets.</b> Raw model confidences (0..1) are grouped into five buckets:
/// <c>[0,0.5) · [0.5,0.7) · [0.7,0.85) · [0.85,0.95) · [0.95,1.0]</c>. They are deliberately
/// UNEVEN — finer near the top because that is where over-confidence hurts most (a model that
/// says "0.95" but is right half the time is the dangerous case we most want to catch and pull
/// down). The bottom bucket is wide because anything below 0.5 is already "don't trust me".
/// The top bucket's upper bound is INCLUSIVE so a raw confidence of exactly 1.0 has a home.</para>
///
/// <para><b>Smoothing.</b> Each bucket's accept rate is a Beta(1,1) / Laplace-smoothed
/// estimate <c>(accepted+1)/(total+2)</c>, not the naive <c>accepted/total</c>. The prior is a
/// uniform Beta(1,1) — it pulls a tiny sample toward 0.5 (no opinion) instead of letting two
/// decisions swing the curve to a hard 0 or 1. With many samples the prior washes out and the
/// smoothed rate converges to the empirical rate.</para>
///
/// <para><b>Min-sample guards (honesty).</b> A bucket with fewer than
/// <see cref="MinBucketSamples"/> total verdicts is NOT trusted — a raw value landing there
/// passes through uncalibrated rather than showing a number we can't back. And if the whole
/// org has fewer than <see cref="MinOrgSamples"/> usable decisions, calibration is OFF entirely
/// (cold-start raw passthrough).</para>
/// </summary>
public static class ConfidenceCalibration
{
    /// <summary>
    /// Inclusive lower bounds of the five confidence buckets, ascending. The implicit upper
    /// bound of each bucket is the next entry's lower bound (exclusive), except the final
    /// bucket whose upper bound is 1.0 INCLUSIVE.
    /// </summary>
    public static readonly IReadOnlyList<double> BucketLowerBounds = new[] { 0.0, 0.5, 0.7, 0.85, 0.95 };

    /// <summary>
    /// Minimum total verdicts (accepted + rejected) a bucket needs before its smoothed accept
    /// rate is trusted as the calibrated output. Below this the raw confidence passes through.
    /// Chosen at 8: large enough that the Beta(1,1) prior has washed out enough to mean
    /// something, small enough that a real org reaches it within a few dozen resolved orders.
    /// </summary>
    public const int MinBucketSamples = 8;

    /// <summary>
    /// Minimum total usable decisions across ALL buckets before calibration is enabled for the
    /// org at all. Below this floor every raw value passes through (cold-start). Set equal to
    /// one trusted bucket's worth (8) — a single hot bucket is enough to start calibrating.
    /// </summary>
    public const int MinOrgSamples = 8;

    /// <summary>The basis string shown when a calibrated, evidence-backed number is returned.</summary>
    public static string CalibratedBasis(int sampleSize) =>
        $"calibrated from {sampleSize} of your past decisions";

    /// <summary>The basis string shown when calibration is NOT active (raw passthrough).</summary>
    public const string UncalibratedBasis = "model estimate — not enough history yet";

    /// <summary>
    /// Returns the zero-based index of the bucket a raw confidence value falls into. Values are
    /// clamped to [0,1] first, so any out-of-range raw input still lands in a valid bucket. The
    /// top bucket is inclusive of 1.0.
    /// </summary>
    public static int BucketIndexFor(double rawConfidence)
    {
        var c = Math.Clamp(rawConfidence, 0.0, 1.0);

        // Walk from the highest lower-bound down; the first bound c clears is its bucket.
        for (var i = BucketLowerBounds.Count - 1; i >= 0; i--)
        {
            if (c >= BucketLowerBounds[i])
                return i;
        }

        return 0; // unreachable for c in [0,1] since BucketLowerBounds[0] == 0.0
    }

    /// <summary>The inclusive lower bound of bucket <paramref name="index"/>.</summary>
    public static double LowerBoundOf(int index) => BucketLowerBounds[index];

    /// <summary>
    /// The upper bound of bucket <paramref name="index"/>: the next bucket's lower bound, or
    /// 1.0 for the last bucket (which is inclusive).
    /// </summary>
    public static double UpperBoundOf(int index) =>
        index + 1 < BucketLowerBounds.Count ? BucketLowerBounds[index + 1] : 1.0;

    /// <summary>
    /// The Beta(1,1) / Laplace-smoothed accept rate for a bucket: <c>(accepted+1)/(total+2)</c>.
    /// With zero data this returns 0.5 (the uniform prior's mean).
    /// </summary>
    public static double SmoothedAcceptRate(int accepted, int total) =>
        (accepted + 1.0) / (total + 2.0);
}
