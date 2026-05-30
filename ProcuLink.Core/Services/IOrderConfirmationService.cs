using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// A supplier confirmation as supplied by a manual entry form or a (stubbed) file upload.
/// This is the deterministic, already-parsed shape the service persists and evaluates.
/// API/EDI ingestion is intentionally out of scope for v1 — callers hand the service this
/// payload, however they obtained it.
/// </summary>
public sealed record ParsedConfirmationData(
    /// <summary>Optional overriding status from the source document. When null/blank the
    /// service derives the status purely from line-level comparison.</summary>
    string? Status,
    string? SupplierReference,
    string? Notes,
    DateTime? ReceivedAt,
    IReadOnlyList<ParsedConfirmationLineData> Lines);

/// <summary>
/// One confirmation line as supplied by the caller. Quantities/prices/date are the supplier's
/// <b>confirmed</b> values. The line is matched to an ordered purchase-order line by
/// <see cref="PurchaseOrderLineId"/> when present, otherwise by <see cref="LineNumber"/>.
/// <see cref="IsRejected"/> lets the source explicitly reject an individual line.
/// </summary>
public sealed record ParsedConfirmationLineData(
    Guid? PurchaseOrderLineId,
    int LineNumber,
    decimal ConfirmedQuantity,
    decimal ConfirmedUnitPrice,
    DateOnly? ConfirmedDeliveryDate,
    bool IsRejected = false,
    string? Note = null);

public interface IOrderConfirmationService
{
    /// <summary>
    /// Record a supplier confirmation against a purchase order. Matches each confirmation line
    /// to its ordered line, classifies it (confirmed / changed / rejected), and derives the
    /// overall confirmation status:
    ///   • any rejected line, or an explicit Rejected status  → <c>rejected</c>
    ///   • any changed line                                    → <c>needs_review</c>
    ///   • otherwise                                           → <c>accepted</c>.
    /// Throws <see cref="InvalidOperationException"/> if the order does not exist in this org.
    /// </summary>
    Task<OrderConfirmationEntity> RecordAsync(
        Guid orgId, Guid purchaseOrderId, ParsedConfirmationData data, CancellationToken ct);

    /// <summary>
    /// Record a confirmation from an uploaded file (manual upload). The file is stored as-is and a
    /// confirmation is created in <c>needs_review</c> with <c>source = "manual_upload"</c> and no
    /// parsed lines — a human reviews the document. Automatic parsing of the file is a later concern.
    /// Throws <see cref="InvalidOperationException"/> if the order does not exist in this org.
    /// </summary>
    Task<OrderConfirmationEntity> RecordUploadStubAsync(
        Guid orgId, Guid purchaseOrderId, Stream fileStream,
        string fileName, string contentType, CancellationToken ct);

    /// <summary>Get a single confirmation (with lines), org-scoped. Null if not found.</summary>
    Task<OrderConfirmationEntity?> GetAsync(Guid orgId, Guid confirmationId, CancellationToken ct);

    /// <summary>List all confirmations for a purchase order, newest first, org-scoped.</summary>
    Task<IReadOnlyList<OrderConfirmationEntity>> ListForOrderAsync(
        Guid orgId, Guid purchaseOrderId, CancellationToken ct);

    /// <summary>
    /// True when any confirmation on the order has a completion-blocking status (i.e. a
    /// supplier rejection). Used by completion logic so a rejected order is never treated
    /// as done. Org-scoped.
    /// </summary>
    Task<bool> IsOrderBlockedFromCompletionAsync(
        Guid orgId, Guid purchaseOrderId, CancellationToken ct);
}
