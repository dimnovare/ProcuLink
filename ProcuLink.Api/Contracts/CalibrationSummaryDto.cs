using ProcuLink.Core.Services;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// The org-wide confidence-calibration summary returned by <c>GET /api/ai/calibration</c> for a
/// "how reliable are our AI suggestions" insight UI.
///
/// <para><see cref="IsActive"/> is false during cold-start (the org hasn't resolved enough AI
/// suggestions yet) or when no single bucket has enough samples — in that state the UI should
/// show the honest "not enough history yet" state rather than a curve.</para>
/// </summary>
/// <param name="IsActive">
/// True when calibration is currently shaping displayed confidences for this org (org cleared the
/// history floor AND at least one bucket is trusted).
/// </param>
/// <param name="TotalDecisions">
/// Total usable AI verdicts considered (accepted + rejected with a non-null confidence). Excludes
/// manual resolutions and suggestions that never carried a confidence.
/// </param>
/// <param name="MinBucketSamples">The per-bucket trust threshold, echoed so the UI can explain it.</param>
/// <param name="MinOrgSamples">The org-wide activation floor, echoed so the UI can explain it.</param>
/// <param name="Buckets">Every configured confidence bucket, lowest-first.</param>
public sealed record CalibrationSummaryDto(
    bool IsActive,
    int TotalDecisions,
    int MinBucketSamples,
    int MinOrgSamples,
    IReadOnlyList<CalibrationBucketDto> Buckets)
{
    /// <summary>Projects the Core summary into the API contract.</summary>
    public static CalibrationSummaryDto From(CalibrationSummary summary) => new(
        IsActive: summary.IsActive,
        TotalDecisions: summary.TotalDecisions,
        MinBucketSamples: ConfidenceCalibration.MinBucketSamples,
        MinOrgSamples: ConfidenceCalibration.MinOrgSamples,
        Buckets: summary.Buckets
            .Select(b => new CalibrationBucketDto(
                Label: FormatLabel(b.LowerInclusive, b.UpperExclusive),
                LowerInclusive: b.LowerInclusive,
                UpperExclusive: b.UpperExclusive,
                Accepted: b.Accepted,
                Rejected: b.Rejected,
                Total: b.Total,
                SmoothedAcceptRate: b.SmoothedAcceptRate,
                IsTrusted: b.IsTrusted))
            .ToList());

    private static string FormatLabel(double lower, double upper)
    {
        // The top bucket's upper bound is inclusive; render it with a closing bracket so the UI
        // doesn't mislead users into thinking 1.0 is excluded. InvariantCulture so the decimal
        // separator is always "." regardless of the server's locale (this is an API contract).
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var isTop = upper >= 1.0;
        return $"[{lower.ToString("0.##", ci)}, {upper.ToString("0.##", ci)}{(isTop ? "]" : ")")}";
    }
}

/// <summary>One confidence bucket's empirical statistics for the summary UI.</summary>
/// <param name="Label">Human-readable interval, e.g. "[0.85, 0.95)".</param>
/// <param name="LowerInclusive">Bucket lower bound (inclusive).</param>
/// <param name="UpperExclusive">Bucket upper bound (exclusive, except the top bucket).</param>
/// <param name="Accepted">Accepted verdicts that fell in this bucket.</param>
/// <param name="Rejected">Rejected verdicts that fell in this bucket.</param>
/// <param name="Total">Accepted + rejected.</param>
/// <param name="SmoothedAcceptRate">Beta(1,1)-smoothed accept rate (accepted+1)/(total+2).</param>
/// <param name="IsTrusted">True when the bucket has enough samples to drive a calibrated number.</param>
public sealed record CalibrationBucketDto(
    string Label,
    double LowerInclusive,
    double UpperExclusive,
    int Accepted,
    int Rejected,
    int Total,
    double SmoothedAcceptRate,
    bool IsTrusted);
