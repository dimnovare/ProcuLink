namespace ProcuLink.Core.Entities;

public class DeliveryAttempt
{
    public Guid Id { get; set; }
    /// <summary>Null for test-fire attempts not linked to a real order.</summary>
    public Guid? OrderId { get; set; }
    public Guid OrgId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; }
    public int? ResponseCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>1-based attempt index within an order's delivery retry sequence; 0 for test-fire rows.</summary>
    public int AttemptNumber { get; set; }

    // Navigation — Order is optional (null for test-fire rows)
    public PurchaseOrderEntity? Order { get; set; }
    public Organisation Organisation { get; set; } = null!;
}
