namespace ProcuLink.Core.Entities;

public class SupplierPoMapping
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    /// <summary>JSONB column -- serialized PoMappingConfig.</summary>
    public string ConfigJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
