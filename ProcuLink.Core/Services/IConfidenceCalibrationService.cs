namespace ProcuLink.Core.Services;

/// <summary>
/// V9 — confidence calibration (the Learn-loop moat made real). Computes an EMPIRICAL
/// reliability curve from one org's <c>AiSuggestionDecision</c> history (how often a
/// suggestion at raw confidence X was actually accepted) and uses it to translate a raw
/// model confidence into a calibrated, evidence-backed one.
///
/// <para><b>Honesty contract (critical):</b> a calibrated number is only ever returned when
/// it is backed by ≥ <see cref="ConfidenceCalibration.MinBucketSamples"/> real decisions in
/// the relevant bucket AND the org has ≥ <see cref="ConfidenceCalibration.MinOrgSamples"/>
/// total decisions. Otherwise the RAW confidence passes through unchanged and
/// <see cref="CalibrationResult.IsCalibrated"/> is <c>false</c> with a basis string that says
/// so. Calibration is a DISPLAY/derived layer — it NEVER mutates the stored raw confidence,
/// and it NEVER invents a confidence for a suggestion that had none (a null raw input stays
/// null/uncalibrated).</para>
///
/// <para><b>Tenancy:</b> every query is scoped to the org. One org's decisions can never
/// affect another org's curve.</para>
/// </summary>
public interface IConfidenceCalibrationService
{
    /// <summary>
    /// Translates one raw model confidence (0..1) into a calibrated confidence for the given
    /// org, using that org's empirical accept-rate curve. Falls back to the raw value (and
    /// <see cref="CalibrationResult.IsCalibrated"/> = false) when the bucket the raw value
    /// lands in has too few samples, or the org has too little history overall.
    /// </summary>
    Task<CalibrationResult> CalibrateAsync(Guid orgId, double rawConfidence, CancellationToken ct = default);

    /// <summary>
    /// The per-org calibration summary: every confidence bucket with its decision counts and
    /// smoothed accept rate, plus whether calibration is currently active for the org and the
    /// total trusted sample size. Intended for a future "how reliable are our AI suggestions"
    /// insight UI. Org-scoped.
    /// </summary>
    Task<CalibrationSummary> GetCalibrationSummaryAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>
/// The result of calibrating one raw confidence value. <see cref="CalibratedConfidence"/>
/// equals <see cref="RawConfidence"/> exactly when <see cref="IsCalibrated"/> is false.
/// </summary>
/// <param name="RawConfidence">The model's raw confidence, unchanged (0..1).</param>
/// <param name="CalibratedConfidence">
/// The empirically-adjusted confidence when calibrated; otherwise equal to
/// <paramref name="RawConfidence"/>.
/// </param>
/// <param name="IsCalibrated">
/// True only when a real, sufficiently-sampled empirical accept rate backed the calibration.
/// </param>
/// <param name="SampleSize">
/// The number of past decisions in the bucket this raw value fell into (the evidence behind
/// the calibrated number). 0 when the org is below the calibration floor.
/// </param>
/// <param name="Basis">
/// A truthful, user-facing explanation: "calibrated from N of your past decisions" when
/// calibrated, or "model estimate — not enough history yet" when not.
/// </param>
public sealed record CalibrationResult(
    double RawConfidence,
    double CalibratedConfidence,
    bool IsCalibrated,
    int SampleSize,
    string Basis);

/// <summary>One confidence bucket's empirical statistics for the summary view.</summary>
/// <param name="LowerInclusive">Bucket lower bound (inclusive), 0..1.</param>
/// <param name="UpperExclusive">
/// Bucket upper bound. Exclusive for every bucket EXCEPT the top one, whose upper bound is
/// inclusive so a raw confidence of exactly 1.0 has a home.
/// </param>
/// <param name="Accepted">Count of <c>accepted</c> decisions whose raw confidence fell here.</param>
/// <param name="Rejected">Count of <c>rejected</c> decisions whose raw confidence fell here.</param>
/// <param name="SmoothedAcceptRate">
/// The Laplace-/Beta(1,1)-smoothed accept rate <c>(accepted+1)/(total+2)</c>. Smoothing keeps
/// a tiny sample from swinging to a hard 0 or 1.
/// </param>
/// <param name="IsTrusted">
/// True when <c>Accepted + Rejected ≥ MinBucketSamples</c> — i.e. the bucket has enough
/// evidence to drive a calibrated number rather than falling back to raw.
/// </param>
public sealed record CalibrationBucket(
    double LowerInclusive,
    double UpperExclusive,
    int Accepted,
    int Rejected,
    double SmoothedAcceptRate,
    bool IsTrusted)
{
    /// <summary>Total verdicts (accepted + rejected) that landed in this bucket.</summary>
    public int Total => Accepted + Rejected;
}

/// <summary>
/// The org-wide calibration picture: the bucket breakdown plus whether calibration is active.
/// </summary>
/// <param name="IsActive">
/// True when the org has ≥ <see cref="ConfidenceCalibration.MinOrgSamples"/> usable decisions
/// AND at least one bucket is trusted — i.e. at least some raw values would be calibrated.
/// </param>
/// <param name="TotalDecisions">
/// Total usable decisions considered (accepted + rejected with a non-null confidence). Excludes
/// <c>manual</c> and null-confidence rows.
/// </param>
/// <param name="Buckets">Every configured bucket, lowest-first, with its counts and rates.</param>
public sealed record CalibrationSummary(
    bool IsActive,
    int TotalDecisions,
    IReadOnlyList<CalibrationBucket> Buckets);
