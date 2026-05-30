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
    /// <summary>
    /// Set when the supplier endpoint returned a 4xx response indicating an
    /// explicit rejection (as opposed to a transient 5xx / network failure).
    /// </summary>
    public string? RejectionReason { get; set; }
    /// <summary>
    /// Rejection capture: the supplier endpoint's raw response/NACK body, persisted verbatim
    /// (bounded to <see cref="MaxResponseBodyLength"/> chars) for both rejections and transient
    /// failures so operators can diagnose why a delivery was refused. Null when no body was
    /// returned (e.g. network failure before any response).
    /// </summary>
    public string? ResponseBody { get; set; }
    /// <summary>
    /// ACK round-trip: UTC timestamp at which the supplier acknowledged receipt
    /// (HTTP 2xx / successful dispatch). Null until the delivery is confirmed.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }
    /// <summary>1-based attempt index within an order's delivery retry sequence; 0 for test-fire rows.</summary>
    public int AttemptNumber { get; set; }

    /// <summary>Upper bound on persisted <see cref="ResponseBody"/> length — keeps a hostile/huge supplier body from bloating the row.</summary>
    public const int MaxResponseBodyLength = 8_000;

    // Navigation — Order is optional (null for test-fire rows)
    public PurchaseOrderEntity? Order { get; set; }
    public Organisation Organisation { get; set; } = null!;
}
