namespace ProcuLink.Core.Entities;

/// <summary>
/// EF entity for purchase_order_lines.
/// </summary>
public class PurchaseOrderLineEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int LineNumber { get; set; }
    public string BuyerItemCode { get; set; } = string.Empty;
    public string? SupplierItemCode { get; set; }
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal UnitPrice { get; set; }
    public float Confidence { get; set; }
    public bool NeedsReview { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
}
