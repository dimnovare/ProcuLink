namespace ProcuLink.Core.Entities;

/// <summary>
/// Append-only record of a lifecycle event for a purchase order.
/// Written at upload, parse, resolve/correct, AI-accept, transform, and delivery.
/// Never updated or deleted — immutable evidence for the PO Passport.
/// </summary>
public class PoPassportEvent
{
    public Guid     Id         { get; set; }
    public Guid     OrgId      { get; set; }
    public Guid     OrderId    { get; set; }
    /// <summary>Upload | Parse | Map | Transform | Deliver</summary>
    public string   Stage      { get; set; } = string.Empty;
    /// <summary>Created | Succeeded | Failed | Corrected | AiAccepted</summary>
    public string   EventType  { get; set; } = string.Empty;
    /// <summary>user | system | ai</summary>
    public string   ActorType  { get; set; } = "system";
    public string?  ActorId    { get; set; }
    /// <summary>Raw JSON string (stored as jsonb). Serialised by the caller; never a CLR JsonDocument to avoid EF convention-scan issues.</summary>
    public string?  Payload    { get; set; }
    public DateTime OccurredAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
