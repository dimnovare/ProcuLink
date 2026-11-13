namespace ProcuLink.Core.Entities;

public class IntegrationSubscription
{
    public Guid    Id              { get; set; }
    public Guid    OrganisationId  { get; set; }
    /// <summary>"zapier" | "make" | "custom"</summary>
    public string  Platform        { get; set; } = "custom";
    /// <summary>
    /// An order event name (<c>order.*</c>). Nothing fires these any more: plan 13b (2026-11-13)
    /// deleted the order-event webhook sender with the order pipeline. The table stays (no drops).
    /// </summary>
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
