namespace ProcuLink.Core.Entities;

public class IntegrationSubscription
{
    public Guid    Id              { get; set; }
    public Guid    OrganisationId  { get; set; }
    /// <summary>"zapier" | "make" | "custom"</summary>
    public string  Platform        { get; set; } = "custom";
    /// <summary>
    /// One of <see cref="Constants.IntegrationEventTypes.Subscribable"/>. Do not re-list the values
    /// here — this comment previously named three events while five were emitted, and a stale doc
    /// that reads as a registry is how the missing <c>order.rejected</c> entry went unnoticed.
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
