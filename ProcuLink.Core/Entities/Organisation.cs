namespace ProcuLink.Core.Entities;

public class Organisation
{
    public Guid Id { get; set; }
    public string ClerkOrgId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Unique kebab-case slug for machine-to-machine inbound addressing.
    /// Auto-generated at org creation. Never changes after set.
    /// </summary>
    public string Slug { get; set; } = string.Empty;
    public string Plan { get; set; } = "pilot";
    public string AccountStatus { get; set; } = "trialing";
    public DateTime CreatedAt { get; set; }

    // ── Pilot trial tracking ───────────────────────────────────────────────
    /// <summary>Set at org creation. Never updated. Drives Pilot time-window check.</summary>
    public DateTime  TrialStartedAt               { get; set; }
    /// <summary>Set at org creation for Pilot. Historical value remains after upgrade/cancel.</summary>
    public DateTime? TrialEndsAt                  { get; set; }
    /// <summary>Admin-set override. When set, extends the 14-day deadline.</summary>
    public DateTime? PilotExtendedUntil           { get; set; }
    /// <summary>Set when user clicks "Request Pilot extension". Sales signal.</summary>
    public DateTime? PilotExtensionRequestedAt    { get; set; }

    // ── Stripe ────────────────────────────────────────────────────────────
    public string? StripeCustomerId      { get; set; }
    public string? StripeSubscriptionId  { get; set; }
    public string? StripePriceId         { get; set; }
    public string? StripeSubscriptionStatus { get; set; }
    public string? BillingEmail          { get; set; }
    public DateTime? BillingUpdatedAt    { get; set; }
    public string EmailConfigJson        { get; set; } = "{}";

    // Navigation
    public List<Membership> Memberships { get; set; } = new();
    public List<Supplier> Suppliers { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
    public List<AuditEvent> AuditEvents { get; set; } = new();
    public List<TenantApiKey>             ApiKeys                  { get; set; } = new();
    public List<IntegrationSubscription>  IntegrationSubscriptions { get; set; } = new();
}
