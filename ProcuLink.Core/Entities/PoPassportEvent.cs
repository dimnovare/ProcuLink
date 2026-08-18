namespace ProcuLink.Core.Entities;

/// <summary>
/// Append-only record of a lifecycle event for a purchase order.
/// Written at upload, parse, resolve/correct, AI-accept, transform, and delivery.
///
/// <para><b>Append-only in normal operation, NOT undeletable.</b> This comment used to end
/// "never updated or deleted — immutable evidence for the PO Passport", which was false in
/// two directions and had been since both deleters shipped:
/// <list type="bullet">
///   <item><description><c>DataErasureService.EraseOrderAsync</c> hard-deletes an order's rows
///   via <c>RemoveRange</c>. It must: a GDPR erasure that preserved the order's lifecycle trail
///   would not be an erasure.</description></item>
///   <item><description><c>DataRetentionService.RunAsync</c> prunes rows older than
///   <c>DataRetentionOptions.PassportEventDays</c> (default 180). Off by default
///   (<c>Enabled = false</c>), so today this is latent rather than active.</description></item>
/// </list>
/// Nothing UPDATES a row — that half of the old claim holds. Do not restore the deleted half,
/// and do not let published copy call this trail immutable or tamper-proof: a frontend guard
/// (<c>gatedCapabilityClaims.test.ts</c>) already fails buyer-facing copy that does.</para>
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
