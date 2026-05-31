namespace ProcuLink.Core.Entities;

public class ItemMapping
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    public string BuyerItemCode { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;
    public float Confidence { get; set; }

    /// <summary>manual | imported | suggested</summary>
    public string Source { get; set; } = "manual";

    /// <summary>
    /// Number of times this mapping has been saved or reaffirmed via manual
    /// resolution (UpsertAsync). Not incremented by automatic resolution.
    /// </summary>
    public int AppliedCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
