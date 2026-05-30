namespace ProcuLink.Core.Entities;

/// <summary>
/// A supplier's confirmation (acknowledgement) of a purchase order — the inbound
/// counterpart to the outbound PO. Answers the operator's core question: did the
/// supplier <b>accept</b>, <b>reject</b>, or <b>change</b> the order (qty / date / price),
/// which lines are confirmed, and what needs human review?
///
/// One purchase order may receive more than one confirmation over time (e.g. an initial
/// acknowledgement followed by a revision), so this is a one-to-many child of
/// <see cref="PurchaseOrderEntity"/> rather than a 1:1 record.
///
/// Status values come from <see cref="ProcuLink.Core.Constants.OrderConfirmationStatus"/>.
/// </summary>
public class OrderConfirmationEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning organisation (tenant). Every query is scoped by this.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The purchase order this confirmation acknowledges.</summary>
    public Guid PurchaseOrderId { get; set; }

    /// <summary>
    /// Lifecycle status — see <see cref="ProcuLink.Core.Constants.OrderConfirmationStatus"/>:
    /// sent | accepted | accepted_with_changes | needs_review | rejected | no_response.
    /// </summary>
    public string Status { get; set; } = ProcuLink.Core.Constants.OrderConfirmationStatus.Sent;

    /// <summary>
    /// Supplier's own reference for this confirmation (their order/acknowledgement number),
    /// if supplied. Free text; not unique.
    /// </summary>
    public string? SupplierReference { get; set; }

    /// <summary>
    /// How this confirmation entered the system. Manual entry / upload first; API/EDI later.
    /// e.g. "manual_entry" | "manual_upload". Free text so future sources don't need a migration.
    /// </summary>
    public string Source { get; set; } = "manual_entry";

    /// <summary>Original filename when the confirmation was uploaded as a file (manual upload).</summary>
    public string? SourceFileName { get; set; }

    /// <summary>Storage key for the uploaded confirmation file, when applicable.</summary>
    public string? SourceFileKey { get; set; }

    /// <summary>When the supplier's confirmation was received (UTC). Defaults to receipt time.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>Free-text note from the supplier or the operator (e.g. reason for rejection).</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation? Organisation { get; set; }
    public PurchaseOrderEntity? PurchaseOrder { get; set; }
    public List<OrderConfirmationLineEntity> Lines { get; set; } = new();
}
