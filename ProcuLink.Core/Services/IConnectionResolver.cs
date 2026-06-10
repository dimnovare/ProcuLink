namespace ProcuLink.Core.Services;

/// <summary>
/// Group V1 — resolves the currently-active (published) connection revision for a supplier so
/// an order can be pinned to it at ingest. Returns null when the supplier has no connection or
/// no active published revision (zero-config / pre-backfill); a null pin means "fall back to
/// live config", preserving prior behaviour.
/// </summary>
public interface IConnectionResolver
{
    Task<Guid?> ResolveActiveRevisionAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
