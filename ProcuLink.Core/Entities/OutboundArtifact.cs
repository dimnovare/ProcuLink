namespace ProcuLink.Core.Entities;

public class OutboundArtifact
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string FileKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
    public Organisation Organisation { get; set; } = null!;
}
