using System.ComponentModel.DataAnnotations;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// Manual-entry payload for recording a supplier's confirmation of a purchase order.
/// This is the deterministic, already-structured shape the operator submits from the
/// confirmation form (or that a future parser produces). API/EDI ingestion is out of scope.
/// </summary>
public sealed record RecordConfirmationRequest(
    /// <summary>
    /// Optional explicit overall status from the source document
    /// (sent | accepted | accepted_with_changes | needs_review | rejected | no_response).
    /// When null/blank the status is derived from the line comparison. A rejection always wins.
    /// </summary>
    string? Status,
    [MaxLength(200)] string? SupplierReference,
    [MaxLength(2000)] string? Notes,
    DateTime? ReceivedAt,
    [Required] List<RecordConfirmationLineRequest> Lines);

/// <summary>One confirmation line: the supplier's confirmed quantity/price/date for an ordered line.</summary>
public sealed record RecordConfirmationLineRequest(
    /// <summary>Ordered PO line this corresponds to. When null, the line is matched by LineNumber.</summary>
    Guid? PurchaseOrderLineId,
    int LineNumber,
    decimal ConfirmedQuantity,
    decimal ConfirmedUnitPrice,
    DateOnly? ConfirmedDeliveryDate,
    bool IsRejected = false,
    [MaxLength(1000)] string? Note = null);
