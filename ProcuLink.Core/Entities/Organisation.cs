namespace ProcuLink.Core.Entities;

public class Organisation
{
    public Guid Id { get; set; }
    public string ClerkOrgId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Plan { get; set; } = "pilot";
    public DateTime CreatedAt { get; set; }

    // ── Pilot trial tracking ───────────────────────────────────────────────
    /// <summary>Set at org creation. Never updated. Drives Pilot time-window check.</summary>
    public DateTime  TrialStartedAt               { get; set; } = DateTime.UtcNow;
    /// <summary>Admin-set override. When set, extends the 14-day deadline.</summary>
    public DateTime? PilotExtendedUntil           { get; set; }
    /// <summary>Set when user clicks "Request Pilot extension". Sales signal.</summary>
    public DateTime? PilotExtensionRequestedAt    { get; set; }

    // ── Stripe ────────────────────────────────────────────────────────────
    public string? StripeCustomerId      { get; set; }
    public string? StripeSubscriptionId  { get; set; }

    // Navigation
    public List<Membership> Memberships { get; set; } = new();
    public List<Supplier> Suppliers { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
    public List<AuditEvent> AuditEvents { get; set; } = new();
}
