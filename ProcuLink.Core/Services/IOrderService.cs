using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Core.Services;

/// <summary>
/// Orchestrates the Phase 2 upload-parse-resolve-transform lifecycle for a purchase order.
/// Every method is org-scoped via the explicit <c>organisationId</c> parameter.
/// </summary>
public interface IOrderService
{
    /// <summary>
    /// Upload a raw file to R2, parse it, auto-resolve known item mappings,
    /// persist the order and its lines, and write an audit event.
    /// Returns the saved entity on success, or an error string on a known failure
    /// (unsupported format, empty file, parse error).
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(
        Guid organisationId,
        Guid supplierId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct);

    /// <summary>
    /// Upload a raw file to R2 and create an order stub with status="parsing".
    /// Does NOT parse the file inline — enqueue <c>ParseOrderJob</c> after calling this.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId,
        Guid supplierId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct);

    /// <summary>
    /// Create a purchase order directly from an already-parsed order (e.g. from
    /// the email-body NLP extractor). There is no source file — the order is
    /// persisted with lines populated, auto-resolved against item_mappings, and
    /// no <c>ParseOrderJob</c> is required.
    /// </summary>
    /// <param name="organisationId">Tenant.</param>
    /// <param name="supplierId">Resolved supplier id.</param>
    /// <param name="order">
    /// The already-parsed order. Field-by-field identical in shape to
    /// <c>ProcuLink.Transform.Parsing.ParsedOrder</c>; mapped to it internally
    /// by the implementation (Core cannot reference Transform).
    /// </param>
    /// <param name="source">
    /// Provenance tag stored on the order's canonical JSON, e.g.
    /// <c>"email_body_nlp"</c>. The review UI uses this to show how the order
    /// was created.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The persisted order with status <c>"pending_review"</c> when any line is
    /// unresolved, or <c>"ready"</c> when every line auto-resolved against an
    /// existing item mapping.
    /// </returns>
    Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
        Guid organisationId,
        Guid supplierId,
        ExtractedOrder order,
        string source,
        CancellationToken ct);

    /// <summary>
    /// Parse the stored source file for an order in "parsing" status,
    /// auto-resolve item mappings, persist lines, and advance status to
    /// "pending_review" or "ready".  Idempotent — skips if status != "parsing".
    /// Returns the entity together with column-header metadata detected while
    /// the file buffer was in memory; pass these to
    /// <see cref="Detection.ISchemaFingerprintService.RecordParseSuccessAsync"/> to avoid a
    /// second download.
    /// </summary>
    Task<Result<ParsedFileOutput>> ParseStoredFileAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct);

    /// <summary>Load a single order with its lines and artifacts, scoped to the org.</summary>
    Task<Result<PurchaseOrderEntity>> GetByIdAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct);

    /// <summary>
    /// Paginated, filtered list of orders for the org.
    /// Filters are applied before pagination; totalCount reflects the filtered count.
    /// Results are ordered newest-first (CreatedAt DESC).
    /// </summary>
    /// <param name="organisationId">Tenant scope — every row must match this id.</param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Items per page, already clamped by the caller.</param>
    /// <param name="status">Optional status filter (exact match).</param>
    /// <param name="supplierId">Optional supplier id filter.</param>
    /// <param name="search">
    /// Optional case-insensitive substring match over PO number and supplier name
    /// (evaluated in EF/SQL). Buyer name from canonical_json is matched in memory
    /// after the database query because it lives in a jsonb column.
    /// </param>
    /// <param name="dateFrom">Inclusive lower bound on CreatedAt (UTC).</param>
    /// <param name="dateTo">Inclusive upper bound on CreatedAt (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(
        Guid       organisationId,
        int        page,
        int        pageSize,
        string?    status,
        Guid?      supplierId,
        string?    search,
        DateTime?  dateFrom,
        DateTime?  dateTo,
        CancellationToken ct);

    /// <summary>
    /// Offset/limit window over the same filtered, newest-first order list as
    /// <see cref="ListPagedAsync"/>. This is the underlying primitive: <c>ListPagedAsync</c>
    /// delegates to it with <c>skip = (page-1)*pageSize, take = pageSize</c>. Exposing it lets
    /// callers honour a REST-style arbitrary <c>offset</c> exactly (an offset that is not a
    /// multiple of the page size cannot be expressed as a 1-based page).
    /// Ordering carries a deterministic tiebreaker (CreatedAt DESC, then Id DESC) so windows
    /// never overlap or drop rows when many orders share the same CreatedAt — the realistic
    /// case for a large bulk API ingest.
    /// </summary>
    /// <param name="organisationId">Tenant scope — every row must match this id.</param>
    /// <param name="skip">Number of rows to skip (0-based offset). Negative values are treated as 0.</param>
    /// <param name="take">Maximum rows to return. Already clamped by the caller.</param>
    /// <param name="status">Optional status filter (exact match, with the failure-bucket expansion).</param>
    /// <param name="supplierId">Optional supplier id filter.</param>
    /// <param name="search">Optional case-insensitive substring match over PO number, supplier name, buyer name.</param>
    /// <param name="dateFrom">Inclusive lower bound on CreatedAt (UTC).</param>
    /// <param name="dateTo">Inclusive upper bound on CreatedAt (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(
        Guid       organisationId,
        int        skip,
        int        take,
        string?    status,
        Guid?      supplierId,
        string?    search,
        DateTime?  dateFrom,
        DateTime?  dateTo,
        CancellationToken ct);

    /// <summary>
    /// Transform a fully-resolved order to XML or CSV, upload the artifact to R2,
    /// persist the outbound_artifacts row, and advance the order status to "ready_to_deliver".
    /// Delivery workflow is responsible for "delivering", "delivered", and "delivery_failed".
    /// Returns 422-equivalent failure if any line still has NeedsReview = true.
    /// </summary>
    Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct);

    /// <summary>
    /// Generate a pre-signed R2 download URL (15-minute TTL) for an outbound artifact.
    /// Returns 404-equivalent failure if the artifact does not exist or belongs to another org.
    /// Never streams file bytes through the API — always redirects via signed URL.
    /// </summary>
    Task<Result<DownloadUrl>> GetDownloadUrlAsync(
        Guid organisationId,
        Guid orderId,
        Guid artifactId,
        CancellationToken ct);

    /// <summary>
    /// Apply user-supplied supplier item codes to unresolved lines.
    /// Optionally saves new mappings to the item_mappings table for future auto-resolution.
    /// Recomputes order status: ready if all lines resolved, pending_review otherwise.
    /// </summary>
    /// <param name="header">
    /// Optional corrected header fields (order date / buyer name / currency). Each is
    /// applied only when non-null/non-blank; null means "no change". For buyer name BOTH
    /// the denormalised <c>buyer_name</c> column AND canonical_json are updated so the read
    /// path stays consistent. Pass <c>null</c> (the default) for a lines-only resolve.
    /// </param>
    Task<Result<PurchaseOrderEntity>> ResolveAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<LineResolution> resolutions,
        bool saveMappings,
        CancellationToken ct,
        ResolveHeaderFields? header = null);

    /// <summary>
    /// Manually mark an order as rejected by the supplier — for example when the
    /// rejection arrived via email or a phone call rather than an HTTP response.
    /// Sets status to <c>rejected_by_supplier</c>, writes the rejection reason on
    /// the most-recent delivery attempt (or creates an audit-only entry), and
    /// appends a <c>MarkedRejected</c> audit event.
    /// </summary>
    Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(
        Guid organisationId,
        Guid orderId,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// Bulk-accept AI suggestions for all unresolved lines whose
    /// <c>AiSuggestionConfidence</c> is &gt;= <paramref name="minConfidence"/>.
    /// Clears AI suggestion fields on accepted lines, recomputes order status,
    /// and writes an audit event.
    /// Returns the count of lines that were accepted.
    /// </summary>
    Task<Result<int>> AcceptAiSuggestionsAsync(
        Guid organisationId,
        Guid orderId,
        double minConfidence,
        CancellationToken ct);
}
