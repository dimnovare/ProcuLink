namespace ProcuLink.Core.Entities;

public class IntegrationSubscription
{
    public Guid    Id              { get; set; }
    public Guid    OrganisationId  { get; set; }
    /// <summary>"zapier" | "make" | "custom"</summary>
    public string  Platform        { get; set; } = "custom";
    /// <summary>"order.created" | "order.delivered" | "order.failed"</summary>
    public string  EventType       { get; set; } = string.Empty;
    public string  TargetUrl       { get; set; } = string.Empty;
    /// <summary>AES-GCM encrypted HMAC signing secret.</summary>
    public string? EncryptedSecret { get; set; }
    public bool    IsActive        { get; set; } = true;
    public int     FailureCount    { get; set; } = 0;
    public DateTime CreatedAt      { get; set; }
    public DateTime UpdatedAt      { get; set; }
    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
