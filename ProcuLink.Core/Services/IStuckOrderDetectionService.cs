namespace ProcuLink.Core.Services;

/// <summary>
/// Detects orders stuck mid-pipeline (status 'parsing' or 'transforming') past a
/// timeout and re-drives them so they stop hanging silently. A strand is first
/// re-queued up to a cap; past the cap a 'parsing' strand is dead-lettered to terminal
/// 'failed', while a 'transforming' strand (already fully resolved — a real failing
/// transform reverts itself to 'ready') is recovered to the re-sendable 'ready' state,
/// never falsely failed. Runs as a recurring cross-tenant Hangfire sweep; each order and
/// its audit event carry that order's own OrgId.
/// </summary>
public interface IStuckOrderDetectionService
{
    /// <summary>
    /// Acts on every order older than <paramref name="stuckThreshold"/> still in a
    /// transient pipeline status ('parsing' / 'transforming'): re-queues it (audit
    /// 'StuckRequeued'), or past the cap dead-letters a 'parsing' strand to 'failed'
    /// (audit 'StuckTimeout') / recovers a 'transforming' strand to 'ready' (audit
    /// 'StuckTransformRecovered'). Idempotent. Returns the number acted on.
    /// </summary>
    Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct);
}
