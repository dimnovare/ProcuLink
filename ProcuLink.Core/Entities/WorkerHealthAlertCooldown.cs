namespace ProcuLink.Core.Entities;

/// <summary>
/// One row per operator-alert condition, holding the anti-spam state that used to live only in a
/// process-local singleton.
///
/// <para><b>Why this table exists.</b> The alert sweep's cooldown was in-memory, so every Worker
/// restart re-armed every condition's healthy→bad transition and restarted the
/// <c>WorkerHealthAlert:MinAlertIntervalMinutes</c> window. A crash-looping Worker therefore emailed
/// on the raw 5-minute sweep interval — observed live on 2026-08-20 as 14:50 / 14:55 / 15:00 / 15:05
/// during a run of Railway redeploys, against the 30-minute spacing on either side of it. The flood
/// arrived precisely during the incident the alerts exist to report.</para>
///
/// <para><b>Deliberately NOT organisation-scoped.</b> These conditions describe the health of the
/// deployment — all-org backlogs, the worker heartbeat, the sweep's own blindness — not of any one
/// tenant, so there is no <c>OrgId</c> to scope by and no query filter applies.</para>
/// </summary>
public class WorkerHealthAlertCooldown
{
    /// <summary>
    /// Primary key — the condition identifier from <c>OperationalAlertKeys</c>. The key is the
    /// de-duplication unit, so the cooldown is stored per key exactly as it is decided per key.
    /// </summary>
    public string AlertKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether the condition was degraded at the end of the last sweep. This is the healthy→bad
    /// transition flag: persisting it is what stops a restart from treating an ongoing incident as
    /// a brand-new one.
    /// </summary>
    public bool WasBad { get; set; }

    /// <summary>
    /// When this condition last actually alerted, or <c>null</c> when it never has. Null rather
    /// than <c>DateTime.MinValue</c>: a sentinel timestamp is not a real instant, and Npgsql
    /// rejects a non-UTC <c>DateTime.MinValue</c> against <c>timestamptz</c> anyway.
    /// </summary>
    public DateTime? LastAlertUtc { get; set; }

    /// <summary>When the row was last written. Operator-facing only — nothing branches on it.</summary>
    public DateTime UpdatedUtc { get; set; }
}
