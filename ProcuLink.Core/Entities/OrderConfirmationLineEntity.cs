namespace ProcuLink.Core.Entities;

/// <summary>
/// A single line on a supplier order confirmation, matched back to the ordered
/// <see cref="PurchaseOrderLineEntity"/>. Carries both the <b>ordered</b> values (snapshotted
/// at confirmation time) and the supplier's <b>confirmed</b> values, plus a per-line
/// <see cref="State"/> (confirmed / changed / rejected) so the UI can show exactly what the
/// supplier altered.
/// </summary>
public class OrderConfirmationLineEntity
{
    public Guid Id { get; set; }

    /// <summary>Parent confirmation.</summary>
    public Guid OrderConfirmationId { get; set; }

    /// <summary>Owning organisation (tenant). Denormalised for org-scoped queries/guards.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The ordered purchase-order line this confirmation line corresponds to.
    /// Nullable: a supplier confirmation may reference a line we cannot match (extra/unknown line).
    /// </summary>
    public Guid? PurchaseOrderLineId { get; set; }

    /// <summary>Line number as it appears on the confirmation (best-effort mirror of the PO line).</summary>
    public int LineNumber { get; set; }

    /// <summary>Buyer item code carried for display/matching context.</summary>
    public string? BuyerItemCode { get; set; }

    /// <summary>Supplier item code carried for display/matching context.</summary>
    public string? SupplierItemCode { get; set; }

    // ── Ordered values (snapshot of what we asked for) ───────────────────────────
    public decimal OrderedQuantity { get; set; }
    public decimal OrderedUnitPrice { get; set; }

    /// <summary>Ordered/requested delivery date, if known at confirmation time.</summary>
    public DateOnly? OrderedDeliveryDate { get; set; }

    // ── Confirmed values (what the supplier committed to) ────────────────────────
    public decimal ConfirmedQuantity { get; set; }
    public decimal ConfirmedUnitPrice { get; set; }

    /// <summary>Supplier's confirmed/promised delivery date, if supplied.</summary>
    public DateOnly? ConfirmedDeliveryDate { get; set; }

    /// <summary>
    /// Per-line outcome — see <see cref="ProcuLink.Core.Constants.OrderConfirmationLineState"/>:
    /// confirmed | changed | rejected. Set by the service when matching ordered vs confirmed values.
    /// </summary>
    public string State { get; set; } = ProcuLink.Core.Constants.OrderConfirmationLineState.Confirmed;

    /// <summary>Optional supplier/operator note for this specific line (e.g. partial availability).</summary>
    public string? Note { get; set; }

    // Navigation
    public OrderConfirmationEntity? OrderConfirmation { get; set; }
    public PurchaseOrderLineEntity? PurchaseOrderLine { get; set; }
}
