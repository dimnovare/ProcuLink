using ProcuLink.Core.Entities;

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
    /// Parse the stored source file for an order in "parsing" status,
    /// auto-resolve item mappings, persist lines, and advance status to
    /// "pending_review" or "ready".  Idempotent — skips if status != "parsing".
    /// </summary>
    Task<Result<PurchaseOrderEntity>> ParseStoredFileAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct);

    /// <summary>Load a single order with its lines and artifacts, scoped to the org.</summary>
    Task<Result<PurchaseOrderEntity>> GetByIdAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct);

    /// <summary>List all orders for the org, newest first, as lightweight summaries.</summary>
    Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(
        Guid organisationId,
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
    Task<Result<PurchaseOrderEntity>> ResolveAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<LineResolution> resolutions,
        bool saveMappings,
        CancellationToken ct);
}
