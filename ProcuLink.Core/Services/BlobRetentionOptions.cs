namespace ProcuLink.Core.Services;

/// <summary>
/// Tunables for the per-org BLOB retention sweep (file blobs only — never DB rows).
/// Bound from configuration section <c>Retention</c>.
/// <para>
/// TWO safety latches, BOTH default to "delete nothing":
/// <list type="number">
///   <item><description>PER-ORG opt-in: <c>organisations.retention_days</c> is NULL by default —
///   an org with no value is never touched, full stop.</description></item>
///   <item><description>GLOBAL dry-run: <see cref="DryRun"/> defaults to TRUE, so even an
///   opted-in org only gets a "would delete" audit row until the founder deliberately sets
///   <c>Retention:DryRun=false</c> (env form <c>Retention__DryRun=false</c>) on the Worker.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class BlobRetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>
    /// Second safety latch. TRUE (default): the sweep only audits what it WOULD delete —
    /// no storage delete is ever issued, no purge timestamp is ever written. Must be
    /// explicitly set to false to arm real deletion.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Max candidate ORDERS processed per organisation per run. Bounds each run so a large
    /// backlog drains over several daily runs instead of one unbounded sweep. Default 500.
    /// </summary>
    public int OrderBatchSize { get; set; } = 500;

    /// <summary>Effective per-org order batch cap (never non-positive).</summary>
    public int EffectiveOrderBatchSize => OrderBatchSize > 0 ? OrderBatchSize : 500;
}
