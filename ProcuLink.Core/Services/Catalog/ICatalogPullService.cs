namespace ProcuLink.Core.Services.Catalog;

/// <summary>
/// The single hardened fetch pipeline for supplier-catalog pull sources (SFTP / FTP / FTPS).
/// Owns, in order: SSRF guard immediately before connect → 30 s connect/operation timeouts
/// plus a 5-minute overall deadline → bounded streamed download (ingress byte cap) →
/// SHA-256 unchanged-skip → shared parse → idempotent catalog upsert → honest
/// <c>last_sync_*</c> bookkeeping with enumerated safe error messages only.
/// </summary>
public interface ICatalogPullService
{
    /// <summary>
    /// Runs one full pull for the given source. Persists the SUCCESS outcome
    /// (<c>ok</c>/<c>unchanged</c> + counts + file hash, error cleared) itself; on failure
    /// it throws a sanitized <see cref="CatalogSyncException"/> (no inner exception, safe
    /// message only) and persists nothing — the calling job persists the <c>failed</c>
    /// status before rethrowing. Idempotent: the sink is a natural-key upsert and the
    /// hash-skip makes retries of an unchanged file free.
    /// </summary>
    Task<CatalogPullResult> PullAsync(Guid orgId, Guid sourceId, CancellationToken ct);

    /// <summary>
    /// Read-only honesty probe over the SAME fetch path (guard, timeouts, bounded read,
    /// parse all run identically) using the supplier's SAVED source config, with ONE deliberate
    /// difference: this call runs inside the request-serving API process, so it is bounded by
    /// <c>CatalogLimits.MaxInteractiveTestFetchBytes</c> (32 MB) instead of the 256 MB ingestion
    /// cap <see cref="PullAsync"/> keeps in the Worker. It samples enough to prove credentials,
    /// reachability and column shape; it is not a sync. Never writes:
    /// no upsert, no <c>last_sync_*</c> mutation. Failures come back as
    /// <c>Ok = false</c> with the same enumerated safe message a real sync would persist.
    /// </summary>
    Task<CatalogTestFetchResult> TestFetchAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}

/// <summary>Successful pull outcome (persisted by the service before returning).</summary>
public sealed record CatalogPullResult(
    string Status,      // "ok" | "unchanged"
    int Created,
    int Updated,
    int Skipped,
    string FileHash);

/// <summary>
/// Test-fetch honesty report: what the file looks like, which columns ProcuLink would
/// actually read, and a small sample of what would be imported.
/// </summary>
public sealed record CatalogTestFetchResult(
    bool Ok,
    string? Error,
    string? FileName,
    long? Bytes,
    string? DetectedFormat,
    IReadOnlyList<string> HeaderColumns,
    IReadOnlyDictionary<string, string> MappedFields,
    IReadOnlyList<string> UnmappedColumns,
    int ParsedRows,
    int RowsWithCode,
    IReadOnlyList<CatalogSampleRow> SampleRows)
{
    public static CatalogTestFetchResult Failure(string safeError) => new(
        Ok: false, Error: safeError, FileName: null, Bytes: null, DetectedFormat: null,
        HeaderColumns: Array.Empty<string>(),
        MappedFields: new Dictionary<string, string>(),
        UnmappedColumns: Array.Empty<string>(),
        ParsedRows: 0, RowsWithCode: 0,
        SampleRows: Array.Empty<CatalogSampleRow>());
}

/// <summary>One sample row of the parsed catalog (≤5 returned by test-fetch).</summary>
public sealed record CatalogSampleRow(
    string Code,
    string? Name,
    string? Unit,
    decimal? Price,
    string? Currency,
    string? Barcode,
    string? ExternalId,
    // Shown in the test-fetch honesty report so a founder configuring a vendor feed can SEE
    // whether the manufacturer part column landed — the whole point of the report is that a
    // mis-mapped column is visible before the feed is enabled, not after 14,000 rows import.
    string? ManufacturerPartNumber = null,
    string? ManufacturerName = null);
