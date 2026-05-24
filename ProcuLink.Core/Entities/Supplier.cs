namespace ProcuLink.Core.Entities;

public class Supplier
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Soft-delete timestamp. Null = active.</summary>
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public List<SupplierProfileEntity> SupplierProfiles { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<SupplierPoMapping> PoMappings { get; set; } = new();
}
