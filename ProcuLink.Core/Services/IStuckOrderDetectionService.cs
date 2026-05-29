namespace ProcuLink.Core.Services;

/// <summary>
/// Detects orders stuck mid-pipeline (status 'parsing' or 'transforming') past a
/// timeout and fails them so they stop hanging silently.
/// Runs as a recurring cross-tenant Hangfire sweep; each failed order and its audit
/// event carry that order's own OrgId.
/// </summary>
public interface IStuckOrderDetectionService
{
    /// <summary>
    /// Marks every order older than <paramref name="stuckThreshold"/> still in a
    /// transient pipeline status ('parsing' / 'transforming') as 'failed', writing a
    /// 'StuckTimeout' audit event per order. Idempotent. Returns the number marked.
    /// </summary>
    Task<int> RunAsync(TimeSpan stuckThreshold, CancellationToken ct);
}
