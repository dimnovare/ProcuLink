namespace ProcuLink.Core.Entities;

public class DeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; }
    public int? ResponseCode { get; set; }
    public string? ErrorMessage { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
    public Organisation Organisation { get; set; } = null!;
}
