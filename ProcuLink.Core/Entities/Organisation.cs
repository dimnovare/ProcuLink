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

    // ── Per-org admin limit/trial overrides ─────────────────────────────────
    // All nullable + additive. The EFFECTIVE limit/trial-end is the override
    // when set, otherwise the plan/computed default. Set only by the platform
    // admin surface (POST /api/admin/organisations/{id}/limits) so the founder
    // can grant a prospect extra headroom or a beefier Pilot without changing
    // the plan. Null = no override → plan/computed default applies.
    /// <summary>Admin override for the monthly order limit. Null ⇒ plan default. Non-negative.</summary>
    public int?      OrderLimitOverride           { get; set; }
    /// <summary>Admin override for the supplier limit. Null ⇒ plan default. Non-negative.</summary>
    public int?      SupplierLimitOverride        { get; set; }
    /// <summary>Admin override for the Pilot trial end. Null ⇒ computed default (trial_started_at + 14d).</summary>
    public DateTime? TrialEndsAtOverride          { get; set; }

    // ── Stripe ────────────────────────────────────────────────────────────
    public string? StripeCustomerId      { get; set; }
    public string? StripeSubscriptionId  { get; set; }
    public string? StripePriceId         { get; set; }
    public string? StripeSubscriptionStatus { get; set; }
    public string? BillingEmail          { get; set; }
    public DateTime? BillingUpdatedAt    { get; set; }
    public string EmailConfigJson        { get; set; } = "{}";

    /// <summary>
    /// Indexed flag mirroring "has a non-empty email_config" — the email-poller
    /// dispatcher candidate predicate. Kept in lock-step with EmailConfigJson
    /// wherever it is written (EmailSettingsService) so the poller filters on an
    /// indexed boolean instead of a jsonb-string scan (audit §1.1.F / §2.3.3).
    /// </summary>
    public bool EmailPollingEnabled      { get; set; }

    // ── Webhook receive ingress (Group N Phase 4) ────────────────────────
    /// <summary>
    /// AES-GCM-encrypted HMAC shared secret used to verify inbound webhook calls
    /// at /api/webhook-ingress/{slug}/*. Separate from TenantApiKey because the
    /// raw key must be re-derivable to verify HMAC signatures, but TenantApiKey
    /// only stores a hash. Set/rotated by org admins; receiver-only — never
    /// exposed back to callers after creation.
    /// </summary>
    public string? WebhookSecretEncrypted { get; set; }

    /// <summary>
    /// When true, this org's PDFs are parsed with NO egress to OpenAI: scanned/textless
    /// pages are OCR'd by the self-hosted RapidOcrNet engine and parsed deterministically,
    /// never sent to the OpenAI text/vision extractor. Additive; defaults to false
    /// (existing behaviour). Only effective when the engine is enabled (NoEgressOcr:Enabled).
    /// </summary>
    public bool SelfHostedOcr { get; set; }

    /// <summary>
    /// Per-org presentation flag for the primary order flow. Outbound (default) =
    /// this org is the buyer sending POs out; Inbound = this org is the supplier
    /// receiving customer POs. Direction-agnostic data model underneath — this only
    /// changes how the flow is framed for the org. Additive; defaults to Outbound
    /// (existing behaviour). Does NOT affect delivery routing or the Supplier entity.
    /// </summary>
    public OrderDirection OrderDirection { get; set; } = OrderDirection.Outbound;

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
