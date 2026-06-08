namespace ProcuLink.Core.Entities;

public class Supplier
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Soft-delete timestamp. Null = active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Optional code used by the sample-order path to mark the hidden <c>__sample__</c> supplier; null for real suppliers.</summary>
    public string? Code { get; set; }

    /// <summary>True for the hidden sample supplier created by the sample-order onboarding path.</summary>
    public bool IsSample { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public List<SupplierProfileEntity> SupplierProfiles { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<SupplierPoMapping> PoMappings { get; set; } = new();
    public List<SupplierDeliveryConfig> DeliveryConfigs { get; set; } = new();

    /// <summary>The supplier's product catalog — the authoritative set of real codes (optional).</summary>
    public List<SupplierProduct> Products { get; set; } = new();
}
