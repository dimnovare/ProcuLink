namespace ProcuLink.Core.Entities;

public class Organisation
{
    public Guid Id { get; set; }
    public string ClerkOrgId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public List<Membership> Memberships { get; set; } = new();
    public List<Supplier> Suppliers { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
    public List<AuditEvent> AuditEvents { get; set; } = new();
}
