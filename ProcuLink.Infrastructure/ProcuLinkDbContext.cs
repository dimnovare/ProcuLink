using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure;

public class ProcuLinkDbContext : DbContext, IDataProtectionKeyContext
{
    public ProcuLinkDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierProfileEntity> SupplierProfiles => Set<SupplierProfileEntity>();
    public DbSet<PurchaseOrderEntity> PurchaseOrders => Set<PurchaseOrderEntity>();
    public DbSet<PurchaseOrderLineEntity> PurchaseOrderLines => Set<PurchaseOrderLineEntity>();
    public DbSet<ItemMapping> ItemMappings => Set<ItemMapping>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<AiSuggestionDecision> AiSuggestionDecisions => Set<AiSuggestionDecision>();
    public DbSet<OutboundArtifact> OutboundArtifacts => Set<OutboundArtifact>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupplierPoMapping> SupplierPoMappings => Set<SupplierPoMapping>();
    public DbSet<SupplierDeliveryConfig> SupplierDeliveryConfigs => Set<SupplierDeliveryConfig>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<AiUsageMonthly> AiUsageMonthly => Set<AiUsageMonthly>();
    public DbSet<OverageBillingRecord> OverageBillingRecords => Set<OverageBillingRecord>();
    public DbSet<SftpIngressConfig> SftpIngressConfigs => Set<SftpIngressConfig>();
    public DbSet<ImportedSftpFile> ImportedSftpFiles => Set<ImportedSftpFile>();
    public DbSet<S3IngressConfig> S3IngressConfigs => Set<S3IngressConfig>();
    public DbSet<ImportedS3Object> ImportedS3Objects => Set<ImportedS3Object>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<ValidationRule> ValidationRules => Set<ValidationRule>();
    public DbSet<OutputTemplate> OutputTemplates => Set<OutputTemplate>();
    public DbSet<InvoiceEntity>                Invoices                  => Set<InvoiceEntity>();
    public DbSet<InvoiceLineEntity>            InvoiceLines              => Set<InvoiceLineEntity>();
    public DbSet<AdvanceShippingNoticeEntity>  AdvanceShippingNotices    => Set<AdvanceShippingNoticeEntity>();
    public DbSet<AsnPackageEntity>             AsnPackages               => Set<AsnPackageEntity>();
    public DbSet<AsnPackageLineEntity>         AsnPackageLines           => Set<AsnPackageLineEntity>();
    public DbSet<TenantApiKey>            TenantApiKeys            => Set<TenantApiKey>();
    public DbSet<IntegrationSubscription> IntegrationSubscriptions => Set<IntegrationSubscription>();
    public DbSet<SchemaFingerprint>       SchemaFingerprints       => Set<SchemaFingerprint>();
    public DbSet<OrderConfirmationEntity>     OrderConfirmations     => Set<OrderConfirmationEntity>();
    public DbSet<OrderConfirmationLineEntity> OrderConfirmationLines => Set<OrderConfirmationLineEntity>();
    public DbSet<MappingCorrection>  MappingCorrections  { get; set; } = null!;
    public DbSet<PoPassportEvent>    PoPassportEvents    { get; set; } = null!;
    public DbSet<OrderException>              OrderExceptions              { get; set; } = null!;
    public DbSet<SupplierAcceptanceProfile>   SupplierAcceptanceProfiles   { get; set; } = null!;
    public DbSet<SupplierAcceptanceRule>      SupplierAcceptanceRules      { get; set; } = null!;
    public DbSet<OrderValidationResult>       OrderValidationResults       { get; set; } = null!;
    // ── Group V1: versioned Supplier Connection ─────────────────────────────
    public DbSet<SupplierConnection>             SupplierConnections           { get; set; } = null!;
    public DbSet<SupplierConnectionRevision>     SupplierConnectionRevisions   { get; set; } = null!;
    public DbSet<ConnectionRevisionItemMapping>  ConnectionRevisionItemMappings { get; set; } = null!;
    public DbSet<ConnectionRevisionTestCase>     ConnectionRevisionTestCases   { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Prevent EF's convention scan from treating JsonDocument as an owned entity type.
        // Without this, any entity with a JsonDocument? property causes EF to attempt
        // to configure JsonDocument as a related entity, which fails on InMemory and Npgsql.
        // The HasConversion(jsonDocConverter) calls below handle the actual column mapping.
        modelBuilder.Ignore<JsonDocument>();

        // EF InMemory has no native JsonDocument type handler (Npgsql supplies one for Postgres).
        // Register a string round-trip converter so all providers can materialise these columns.
        // ParseJsonDoc is a static helper because expression trees cannot call methods with
        // optional parameters directly (JsonDocument.Parse has optional JsonDocumentOptions).
        var jsonDocConverter = new ValueConverter<JsonDocument?, string?>(
            v => v == null ? null : v.RootElement.GetRawText(),
            v => ParseJsonDoc(v));

        // ── organisations ──────────────────────────────────────────────
        modelBuilder.Entity<Organisation>(b =>
        {
            b.ToTable("organisations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ClerkOrgId).HasColumnName("clerk_org_id").IsRequired();
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Plan).HasColumnName("plan").IsRequired();
            b.Property(x => x.AccountStatus)
             .HasColumnName("account_status")
             .HasDefaultValue("trialing")
             .IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.HasIndex(x => x.ClerkOrgId).IsUnique();
            b.Property(x => x.TrialStartedAt)
             .HasColumnName("trial_started_at")
             .HasColumnType("timestamptz")
             .HasDefaultValueSql("now()");
            b.Property(x => x.TrialEndsAt)
             .HasColumnName("trial_ends_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.PilotExtendedUntil)
             .HasColumnName("pilot_extended_until")
             .HasColumnType("timestamptz");
            b.Property(x => x.PilotExtensionRequestedAt)
             .HasColumnName("pilot_extension_requested_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.OrderLimitOverride)
             .HasColumnName("order_limit_override");
            b.Property(x => x.SupplierLimitOverride)
             .HasColumnName("supplier_limit_override");
            b.Property(x => x.TrialEndsAtOverride)
             .HasColumnName("trial_ends_at_override")
             .HasColumnType("timestamptz");
            b.Property(x => x.StripeCustomerId)
             .HasColumnName("stripe_customer_id");
            b.Property(x => x.StripeSubscriptionId)
             .HasColumnName("stripe_subscription_id");
            b.Property(x => x.StripePriceId)
             .HasColumnName("stripe_price_id");
            b.Property(x => x.StripeSubscriptionStatus)
             .HasColumnName("stripe_subscription_status");
            b.Property(x => x.BillingEmail)
             .HasColumnName("billing_email");
            b.Property(x => x.BillingUpdatedAt)
             .HasColumnName("billing_updated_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.EmailConfigJson)
             .HasColumnName("email_config")
             .HasColumnType("jsonb")
             .HasDefaultValue("{}")
             .IsRequired();
            // Indexed poller-candidate flag — replaces the email_config <> '{}' jsonb scan
            // (audit §1.1.F / §2.3.3). Partial index = only the rows the poller wants.
            b.Property(x => x.EmailPollingEnabled)
             .HasColumnName("email_polling_enabled")
             .HasDefaultValue(false);
            b.HasIndex(x => x.EmailPollingEnabled)
             .HasFilter("email_polling_enabled = true");
            b.Property(x => x.Slug)
             .HasColumnName("slug")
             .HasDefaultValue("")
             .IsRequired();
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.WebhookSecretEncrypted)
             .HasColumnName("webhook_secret_encrypted")
             .HasColumnType("text");
            b.Property(x => x.SelfHostedOcr)
             .HasColumnName("self_hosted_ocr")
             .HasDefaultValue(false);
            b.Property(x => x.OrderDirection)
             .HasColumnName("order_direction")
             .HasConversion<string>()
             .HasMaxLength(16)
             .HasDefaultValue(OrderDirection.Outbound)
             .IsRequired();
        });

        // ── users ──────────────────────────────────────────────────────
        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ClerkUserId).HasColumnName("clerk_user_id").IsRequired();
            b.Property(x => x.Email).HasColumnName("email").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.HasIndex(x => x.ClerkUserId).IsUnique();
        });

        // ── memberships ────────────────────────────────────────────────
        modelBuilder.Entity<Membership>(b =>
        {
            b.ToTable("memberships");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.Role).HasColumnName("role").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.Memberships)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.User)
             .WithMany(x => x.Memberships)
             .HasForeignKey(x => x.UserId);
        });

        // ── suppliers ──────────────────────────────────────────────────
        modelBuilder.Entity<Supplier>(b =>
        {
            b.ToTable("suppliers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            b.Property(x => x.Code).HasColumnName("code");
            b.Property(x => x.IsSample).HasColumnName("is_sample").HasDefaultValue(false);
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.Suppliers)
             .HasForeignKey(x => x.OrgId);
        });

        // ── supplier_po_mappings ───────────────────────────────────────
        modelBuilder.Entity<SupplierPoMapping>(b =>
        {
            b.ToTable("supplier_po_mappings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(x => new { x.OrgId, x.SupplierId }).IsUnique();

            // EF default: Cascade on required FK — matches pattern used for all other child tables.
            // Soft-deleting a Supplier does NOT trigger this cascade; only a hard DELETE would.
            b.HasOne(x => x.Organisation)
                .WithMany()
                .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
                .WithMany(s => s.PoMappings)
                .HasForeignKey(x => x.SupplierId);
        });

        // ── supplier_delivery_configs ──────────────────────────────────
        modelBuilder.Entity<SupplierDeliveryConfig>(b =>
        {
            b.ToTable("supplier_delivery_configs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.Protocol).HasColumnName("protocol").IsRequired();
            b.Property(x => x.AutoDeliver).HasColumnName("auto_deliver").HasDefaultValue(false).ValueGeneratedNever();
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            b.Property(x => x.EncryptedCredentials).HasColumnName("encrypted_credentials").IsRequired();
            b.Property(x => x.OutputFormat).HasColumnName("output_format");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(x => new { x.OrgId, x.SupplierId }).IsUnique();

            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(s => s.DeliveryConfigs)
             .HasForeignKey(x => x.SupplierId);
        });

        // ── supplier_profiles ──────────────────────────────────────────
        modelBuilder.Entity<SupplierProfileEntity>(b =>
        {
            b.ToTable("supplier_profiles");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.AcceptedFormats)
             .HasColumnName("accepted_formats")
             .HasColumnType("text[]");
            b.Property(x => x.RequiredFields)
             .HasColumnName("required_fields")
             .HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.OutputFormat).HasColumnName("output_format").IsRequired();
            b.Property(x => x.DestinationType).HasColumnName("destination_type").IsRequired();
            b.Property(x => x.DestinationConfig)
             .HasColumnName("destination_config")
             .HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.SupplierProfiles)
             .HasForeignKey(x => x.SupplierId);
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasIndex(x => new { x.OrgId, x.SupplierId })
             .IsUnique()
             .HasDatabaseName("IX_supplier_profiles_org_id_supplier_id");
        });

        // ── purchase_orders ────────────────────────────────────────────
        modelBuilder.Entity<PurchaseOrderEntity>(b =>
        {
            b.ToTable("purchase_orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.PoNumber).HasColumnName("po_number").IsRequired();
            b.Property(x => x.BuyerName).HasColumnName("buyer_name");
            b.Property(x => x.OrderDate).HasColumnName("order_date");
            b.Property(x => x.Currency).HasColumnName("currency").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
            b.Property(x => x.CanonicalJson)
             .HasColumnName("canonical_json")
             .HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.IsSample).HasColumnName("is_sample").HasDefaultValue(false);
            // SLA timers (Group O reliability).
            b.Property(x => x.DeliveryDueAt).HasColumnName("delivery_due_at").HasColumnType("timestamptz");
            b.Property(x => x.SlaBreached).HasColumnName("sla_breached").HasDefaultValue(false);
            // Bounded requeue counter for the stuck-order self-heal sweep (additive).
            b.Property(x => x.RequeueCount).HasColumnName("requeue_count").HasDefaultValue(0);
            // Phase 4 enrichment + doc-type classification (nullable).
            b.Property(x => x.SupplierName).HasColumnName("supplier_name");
            b.Property(x => x.SubTotal).HasColumnName("sub_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.TaxTotal).HasColumnName("tax_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.GrandTotal).HasColumnName("grand_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.PaymentTerms).HasColumnName("payment_terms");
            b.Property(x => x.DocumentType).HasColumnName("document_type");
            // Group V1: the connection revision this order was pinned to at ingest (nullable; legacy = null).
            b.Property(x => x.ConnectionRevisionId).HasColumnName("connection_revision_id");
            // Composite indexes for cross-tenant maintenance sweeps and inbox/list queries.
            // (OrgId, Status): inbox list — filter by tenant then status bucket.
            b.HasIndex(x => new { x.OrgId, x.Status })
             .HasDatabaseName("IX_purchase_orders_org_id_status");
            // (Status, UpdatedAt): StuckOrderDetection sweep — status IN (...) AND updated_at < cutoff.
            b.HasIndex(x => new { x.Status, x.UpdatedAt })
             .HasDatabaseName("IX_purchase_orders_status_updated_at");
            // (OrgId, CreatedAt): inbox list default sort — org-scoped ORDER BY created_at DESC.
            b.HasIndex(x => new { x.OrgId, x.CreatedAt })
             .HasDatabaseName("IX_purchase_orders_org_id_created_at");
            // (SlaBreached, DeliveryDueAt): DeliverySlaSweep — NOT sla_breached AND delivery_due_at < now.
            b.HasIndex(x => new { x.SlaBreached, x.DeliveryDueAt })
             .HasDatabaseName("IX_purchase_orders_sla_breached_delivery_due_at");
            // (OrgId, SupplierId): order-list query filtered/grouped by supplier within a tenant.
            b.HasIndex(x => new { x.OrgId, x.SupplierId })
             .HasDatabaseName("IX_purchase_orders_org_id_supplier_id");
            // (OrgId, BuyerName): SQL-native buyer-name search in ListPagedAsync (ILike predicate).
            b.HasIndex(x => new { x.OrgId, x.BuyerName })
             .HasDatabaseName("IX_purchase_orders_org_id_buyer_name");
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.PurchaseOrders)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.PurchaseOrders)
             .HasForeignKey(x => x.SupplierId);
        });

        // ── purchase_order_lines ───────────────────────────────────────
        modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
        {
            b.ToTable("purchase_order_lines");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.LineNumber).HasColumnName("line_number");
            b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code").IsRequired();
            b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.Unit).HasColumnName("unit");
            b.Property(x => x.UnitPrice).HasColumnName("unit_price");
            b.Property(x => x.Confidence).HasColumnName("confidence");
            b.Property(x => x.NeedsReview).HasColumnName("needs_review");
            b.Property(x => x.AiSuggestedSupplierItemCode).HasColumnName("ai_suggested_supplier_item_code");
            b.Property(x => x.AiSuggestionConfidence).HasColumnName("ai_suggestion_confidence");
            b.Property(x => x.AiSuggestionReason).HasColumnName("ai_suggestion_reason");
            b.Property(x => x.AiSuggestionProvenance).HasColumnName("ai_suggestion_provenance");
            // Phase 4 enrichment (nullable).
            b.Property(x => x.LineAmount).HasColumnName("line_amount").HasColumnType("numeric(18,4)");
            b.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            b.Property(x => x.DeliveryDate).HasColumnName("delivery_date");
            b.HasOne(x => x.Order)
             .WithMany(x => x.Lines)
             .HasForeignKey(x => x.OrderId);
            b.HasIndex(x => new { x.OrderId, x.NeedsReview })
             .HasDatabaseName("IX_purchase_order_lines_order_id_needs_review");
        });

        // ── item_mappings ──────────────────────────────────────────────
        modelBuilder.Entity<ItemMapping>(b =>
        {
            b.ToTable("item_mappings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code").IsRequired();
            b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code").IsRequired();
            b.Property(x => x.Confidence).HasColumnName("confidence");
            b.Property(x => x.Source).HasColumnName("source").IsRequired();
            b.Property(x => x.AppliedCount).HasColumnName("applied_count").HasDefaultValue(0);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.BuyerItemCode }).IsUnique();
            // Covers GetAiMappingCandidatesAsync: filter (OrgId, SupplierId) + ORDER BY updated_at DESC (audit §2.3.2).
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.UpdatedAt });
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.ItemMappings)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.ItemMappings)
             .HasForeignKey(x => x.SupplierId);
            b.HasMany<MappingCorrection>().WithOne(x => x.Mapping).HasForeignKey(x => x.MappingId);
        });

        // ── supplier_products ──────────────────────────────────────────
        // The supplier's authoritative product catalog (ground truth). Tenancy and
        // EF-config conventions mirror item_mappings: org+supplier scoped, snake_case,
        // explicit HasColumnName on every property (the migration relies on these).
        modelBuilder.Entity<SupplierProduct>(b =>
        {
            b.ToTable("supplier_products");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.Code).HasColumnName("code").IsRequired();
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Unit).HasColumnName("unit");
            b.Property(x => x.Price).HasColumnName("price").HasColumnType("numeric(18,4)");
            b.Property(x => x.Currency).HasColumnName("currency");
            b.Property(x => x.Barcode).HasColumnName("barcode");
            b.Property(x => x.ExternalId).HasColumnName("external_id");
            b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // One row per real code per (org, supplier) — the upsert key.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Code }).IsUnique();
            // Active-catalog listing / typeahead read path.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.IsActive });
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.Products)
             .HasForeignKey(x => x.SupplierId);
        });

        // ── ai_suggestion_decisions ────────────────────────────────────
        // Durable, append-only decision history for AI mapping suggestions. Tenancy and
        // snake_case conventions mirror the rest of the schema; the unique index is what
        // makes the write idempotent across Hangfire retries / double-submits.
        modelBuilder.Entity<AiSuggestionDecision>(b =>
        {
            b.ToTable("ai_suggestion_decisions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.LineNumber).HasColumnName("line_number");
            b.Property(x => x.SuggestedSupplierItemCode).HasColumnName("suggested_supplier_item_code").IsRequired();
            b.Property(x => x.ChosenSupplierItemCode).HasColumnName("chosen_supplier_item_code");
            b.Property(x => x.CandidateSetJson).HasColumnName("candidate_set_json").HasColumnType("jsonb");
            b.Property(x => x.Confidence).HasColumnName("confidence");
            b.Property(x => x.ModelVersion).HasColumnName("model_version");
            b.Property(x => x.Decision).HasColumnName("decision").IsRequired();
            b.Property(x => x.DecidedBy).HasColumnName("decided_by");
            b.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            // Read path: list an order's decisions newest-first within a tenant.
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.DecidedAt })
             .HasDatabaseName("IX_ai_suggestion_decisions_org_id_order_id_decided_at");
            // Idempotency key: one row per (org, order, line, suggested code, decision).
            // A replayed accept/reject (retry, double-click) UPDATEs in place instead of inserting.
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.LineNumber, x.SuggestedSupplierItemCode, x.Decision })
             .IsUnique()
             .HasDatabaseName("IX_ai_suggestion_decisions_idempotency");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
        });

        // ── outbound_artifacts ─────────────────────────────────────────
        modelBuilder.Entity<OutboundArtifact>(b =>
        {
            b.ToTable("outbound_artifacts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Format).HasColumnName("format").IsRequired();
            b.Property(x => x.FileKey).HasColumnName("file_key").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.HasOne(x => x.Order)
             .WithMany(x => x.OutboundArtifacts)
             .HasForeignKey(x => x.OrderId);
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.OutboundArtifacts)
             .HasForeignKey(x => x.OrgId);
        });

        // ── delivery_attempts ──────────────────────────────────────────
        modelBuilder.Entity<DeliveryAttempt>(b =>
        {
            b.ToTable("delivery_attempts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Channel).HasColumnName("channel").IsRequired();
            b.Property(x => x.Destination).HasColumnName("destination").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.AttemptedAt)
             .HasColumnName("attempted_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.ResponseCode).HasColumnName("response_code");
            b.Property(x => x.ErrorMessage).HasColumnName("error_message");
            b.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
            // Rejection capture (full NACK body) + ACK round-trip timestamp (Group O reliability).
            b.Property(x => x.ResponseBody).HasColumnName("response_body");
            b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Order)
             .WithMany(x => x.DeliveryAttempts)
             .HasForeignKey(x => x.OrderId)
             .IsRequired(false);
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.DeliveryAttempts)
             .HasForeignKey(x => x.OrgId);
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.AttemptedAt })
             .HasDatabaseName("IX_delivery_attempts_org_id_order_id_attempted_at");
        });

        // ── idempotency_keys ───────────────────────────────────────────
        // Composite primary key (OrgId, Key) so two different organisations may
        // legitimately reuse the same client-supplied key string (Stripe-style).
        modelBuilder.Entity<IdempotencyKey>(b =>
        {
            b.ToTable("idempotency_keys");
            b.HasKey(x => new { x.OrgId, x.Key });
            b.Property(x => x.Key).HasColumnName("key").IsRequired();
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz");
        });

        // ── ai_usage_monthly ───────────────────────────────────────────
        modelBuilder.Entity<AiUsageMonthly>(b =>
        {
            b.ToTable("ai_usage_monthly");
            b.HasKey(x => new { x.OrgId, x.Year, x.Month });
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Year).HasColumnName("year");
            b.Property(x => x.Month).HasColumnName("month");
            b.Property(x => x.TokensUsed).HasColumnName("tokens_used");
            b.Property(x => x.UpdatedAt)
             .HasColumnName("updated_at")
             .HasColumnType("timestamptz");
        });

        // ── overage_billing_records ────────────────────────────────────
        // Idempotency ledger for per-order overage charges. The unique
        // (org_id, billing_key) index is what guarantees a replayed Stripe
        // webhook can never bill the same period twice.
        modelBuilder.Entity<OverageBillingRecord>(b =>
        {
            b.ToTable("overage_billing_records");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.BillingKey).HasColumnName("billing_key").IsRequired();
            b.Property(x => x.OverageOrders).HasColumnName("overage_orders");
            b.Property(x => x.AmountCents).HasColumnName("amount_cents");
            b.Property(x => x.StripeInvoiceItemId).HasColumnName("stripe_invoice_item_id");
            b.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.BillingKey }).IsUnique();
        });

        // ── sftp_ingress_configs ───────────────────────────────────────
        modelBuilder.Entity<SftpIngressConfig>(b =>
        {
            b.ToTable("sftp_ingress_configs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Host).HasColumnName("host").IsRequired();
            b.Property(x => x.Port).HasColumnName("port").HasDefaultValue(22);
            b.Property(x => x.Username).HasColumnName("username").IsRequired();
            b.Property(x => x.EncryptedPassword).HasColumnName("encrypted_password").IsRequired();
            b.Property(x => x.RemoteDirectory).HasColumnName("remote_directory").IsRequired();
            b.Property(x => x.DefaultSupplierId).HasColumnName("default_supplier_id");
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne<Organisation>()
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne<Supplier>()
             .WithMany()
             .HasForeignKey(x => x.DefaultSupplierId)
             .OnDelete(DeleteBehavior.SetNull);
            // Poller scans WHERE is_enabled = true across all orgs every 5 min (audit §1.1.F).
            b.HasIndex(x => x.IsEnabled).HasFilter("is_enabled = true");
        });

        // ── imported_sftp_files ────────────────────────────────────────
        modelBuilder.Entity<ImportedSftpFile>(b =>
        {
            b.ToTable("imported_sftp_files");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.RemotePath).HasColumnName("remote_path").IsRequired();
            b.Property(x => x.FileHash).HasColumnName("file_hash").IsRequired();
            b.Property(x => x.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.RemotePath }).IsUnique();
        });

        // ── s3_ingress_configs ─────────────────────────────────────────
        modelBuilder.Entity<S3IngressConfig>(b =>
        {
            b.ToTable("s3_ingress_configs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.BucketName).HasColumnName("bucket_name").IsRequired();
            b.Property(x => x.KeyPrefix).HasColumnName("key_prefix").IsRequired();
            b.Property(x => x.Region).HasColumnName("region").IsRequired();
            b.Property(x => x.ServiceUrl).HasColumnName("service_url");
            b.Property(x => x.AccessKeyId).HasColumnName("access_key_id").IsRequired();
            b.Property(x => x.EncryptedSecretKey).HasColumnName("encrypted_secret_key").IsRequired();
            b.Property(x => x.DefaultSupplierId).HasColumnName("default_supplier_id");
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne<Organisation>()
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne<Supplier>()
             .WithMany()
             .HasForeignKey(x => x.DefaultSupplierId)
             .OnDelete(DeleteBehavior.SetNull);
            // Poller scans WHERE is_enabled = true across all orgs every 5 min (audit §1.1.F).
            b.HasIndex(x => x.IsEnabled).HasFilter("is_enabled = true");
        });

        // ── imported_s3_objects ────────────────────────────────────────
        modelBuilder.Entity<ImportedS3Object>(b =>
        {
            b.ToTable("imported_s3_objects");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.BucketName).HasColumnName("bucket_name").IsRequired();
            b.Property(x => x.ObjectKey).HasColumnName("object_key").IsRequired();
            b.Property(x => x.ETag).HasColumnName("etag").IsRequired();
            b.Property(x => x.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.BucketName, x.ObjectKey }).IsUnique();
        });

        // ── audit_events ───────────────────────────────────────────────
        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            b.Property(x => x.EntityId).HasColumnName("entity_id");
            b.Property(x => x.Action).HasColumnName("action").IsRequired();
            b.Property(x => x.Payload)
             .HasColumnName("payload")
             .HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.AuditEvents)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.User)
             .WithMany(x => x.AuditEvents)
             .HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.OrgId, x.EntityType, x.EntityId, x.CreatedAt })
             .HasDatabaseName("IX_audit_events_org_id_entity_type_entity_id_created_at");
        });

        // ── order_exceptions ───────────────────────────────────────────
        modelBuilder.Entity<OrderException>(b =>
        {
            b.ToTable("order_exceptions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.LineId).HasColumnName("line_id");
            b.Property(x => x.Stage).HasColumnName("stage").IsRequired();
            b.Property(x => x.Code).HasColumnName("code").IsRequired();
            b.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            b.Property(x => x.State).HasColumnName("state").IsRequired();
            b.Property(x => x.Message).HasColumnName("message").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.ResolvedAt).HasColumnName("resolved_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasIndex(x => new { x.OrgId, x.State, x.Severity, x.CreatedAt })
             .HasDatabaseName("IX_order_exceptions_org_id_state_severity_created_at");
            b.HasIndex(x => new { x.OrgId, x.OrderId })
             .HasDatabaseName("IX_order_exceptions_org_id_order_id");
        });

        // ── supplier_acceptance_profiles ────────────────────────────────
        modelBuilder.Entity<SupplierAcceptanceProfile>(b =>
        {
            b.ToTable("supplier_acceptance_profiles");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.VersionNo).HasColumnName("version_no");
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.Protocol).HasColumnName("protocol");
            b.Property(x => x.OutputFormat).HasColumnName("output_format");
            b.Property(x => x.EffectiveFrom).HasColumnName("effective_from").HasColumnType("timestamptz");
            b.Property(x => x.EffectiveTo).HasColumnName("effective_to").HasColumnType("timestamptz");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasMany(x => x.Rules).WithOne(r => r.Profile).HasForeignKey(r => r.ProfileId);
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.VersionNo })
             .IsUnique()
             .HasDatabaseName("IX_supplier_acceptance_profiles_org_supplier_version");
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Status })
             .HasDatabaseName("IX_supplier_acceptance_profiles_org_supplier_status");
        });

        // ── supplier_acceptance_rules ───────────────────────────────────
        modelBuilder.Entity<SupplierAcceptanceRule>(b =>
        {
            b.ToTable("supplier_acceptance_rules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ProfileId).HasColumnName("profile_id");
            b.Property(x => x.Scope).HasColumnName("scope").IsRequired();
            b.Property(x => x.FieldPath).HasColumnName("field_path").IsRequired();
            b.Property(x => x.Operator).HasColumnName("operator").IsRequired();
            b.Property(x => x.ExpectedValue).HasColumnName("expected_value");
            b.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            b.Property(x => x.BlockOnFail).HasColumnName("block_on_fail");
            b.HasIndex(x => x.ProfileId).HasDatabaseName("IX_supplier_acceptance_rules_profile_id");
        });

        // ── order_validation_results ────────────────────────────────────
        modelBuilder.Entity<OrderValidationResult>(b =>
        {
            b.ToTable("order_validation_results");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.ProfileId).HasColumnName("profile_id");
            b.Property(x => x.RuleId).HasColumnName("rule_id");
            b.Property(x => x.LineNumber).HasColumnName("line_number");
            b.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.Code).HasColumnName("code").IsRequired();
            b.Property(x => x.Message).HasColumnName("message").IsRequired();
            b.Property(x => x.DetectedAt).HasColumnName("detected_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasIndex(x => new { x.OrgId, x.OrderId })
             .HasDatabaseName("IX_order_validation_results_org_id_order_id");
        });

        // ── supplier_connections (Group V1 aggregate root) ──────────────
        modelBuilder.Entity<SupplierConnection>(b =>
        {
            b.ToTable("supplier_connections");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.ActiveRevisionId).HasColumnName("active_revision_id");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
            // One connection per supplier (matches every existing loose-config surface).
            b.HasIndex(x => new { x.OrgId, x.SupplierId })
             .IsUnique()
             .HasDatabaseName("IX_supplier_connections_org_supplier");
            // The live pointer — a real FK to the active revision, but with NO navigation property
            // on either side (a second SupplierConnection→Revision navigation would create the
            // two-navigations-to-same-type ambiguity the model validator rejects). The active
            // revision is loaded via an explicit query on ActiveRevisionId. RESTRICT so a pinned
            // revision can never be deleted out from under a connection.
            b.HasOne<SupplierConnectionRevision>()
             .WithMany()
             .HasForeignKey(x => x.ActiveRevisionId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── supplier_connection_revisions (the immutable versioned bundle) ──
        modelBuilder.Entity<SupplierConnectionRevision>(b =>
        {
            b.ToTable("supplier_connection_revisions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ConnectionId).HasColumnName("connection_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.VersionNo).HasColumnName("version_no");
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.EffectiveFrom).HasColumnName("effective_from").HasColumnType("timestamptz");
            b.Property(x => x.EffectiveTo).HasColumnName("effective_to").HasColumnType("timestamptz");
            b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.PublishedBy).HasColumnName("published_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            // Bundle components (jsonb blobs kept component-shaped so existing readers re-point with no reshaping).
            b.Property(x => x.InputMappingJson).HasColumnName("input_mapping_json").HasColumnType("jsonb");
            b.Property(x => x.OutputMappingJson).HasColumnName("output_mapping_json").HasColumnType("jsonb");
            b.Property(x => x.OutputFormat).HasColumnName("output_format");
            b.Property(x => x.DeliveryProtocol).HasColumnName("delivery_protocol");
            b.Property(x => x.DeliveryConfigJson).HasColumnName("delivery_config_json").HasColumnType("jsonb");
            b.Property(x => x.DeliveryAutoDeliver).HasColumnName("delivery_auto_deliver").HasDefaultValue(false);
            b.Property(x => x.CredentialsRef).HasColumnName("credentials_ref");
            b.Property(x => x.AcceptanceProfileId).HasColumnName("acceptance_profile_id");
            b.Property(x => x.AcceptanceVersionNo).HasColumnName("acceptance_version_no");
            b.Property(x => x.CatalogMode).HasColumnName("catalog_mode").IsRequired();
            b.HasOne(x => x.Connection)
             .WithMany(c => c.Revisions)
             .HasForeignKey(x => x.ConnectionId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasMany(x => x.ItemMappings).WithOne(m => m.Revision).HasForeignKey(m => m.RevisionId);
            b.HasMany(x => x.TestCases).WithOne(t => t.Revision).HasForeignKey(t => t.RevisionId);
            // Versioning precedent (mirror supplier_acceptance_profiles).
            b.HasIndex(x => new { x.ConnectionId, x.VersionNo })
             .IsUnique()
             .HasDatabaseName("IX_supplier_connection_revisions_connection_version");
            // Ingest-time "resolve active revision" path.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Status })
             .HasDatabaseName("IX_supplier_connection_revisions_org_supplier_status");
        });

        // ── connection_revision_item_mappings (snapshot of ItemMapping rows) ──
        modelBuilder.Entity<ConnectionRevisionItemMapping>(b =>
        {
            b.ToTable("connection_revision_item_mappings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.RevisionId).HasColumnName("revision_id");
            b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code").IsRequired();
            b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code").IsRequired();
            b.Property(x => x.Confidence).HasColumnName("confidence");
            b.Property(x => x.Source).HasColumnName("source").IsRequired();
            b.HasIndex(x => x.RevisionId)
             .HasDatabaseName("IX_connection_revision_item_mappings_revision_id");
        });

        // ── connection_revision_test_cases (test pack; empty for backfilled rev-1) ──
        modelBuilder.Entity<ConnectionRevisionTestCase>(b =>
        {
            b.ToTable("connection_revision_test_cases");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.RevisionId).HasColumnName("revision_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.SampleSourceFileKey).HasColumnName("sample_source_file_key");
            b.Property(x => x.ExpectedOutputKey).HasColumnName("expected_output_key");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.HasIndex(x => x.RevisionId)
             .HasDatabaseName("IX_connection_revision_test_cases_revision_id");
        });

        // ── buyers ─────────────────────────────────────────────────────
        modelBuilder.Entity<Buyer>(b =>
        {
            b.ToTable("buyers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Code).HasColumnName("code").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
        });

        // ── validation_rules ───────────────────────────────────────────
        modelBuilder.Entity<ValidationRule>(b =>
        {
            b.ToTable("validation_rules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Description).HasColumnName("description").IsRequired();
            b.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            b.Property(x => x.Entity).HasColumnName("entity").IsRequired();
            b.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
            b.Property(x => x.AutoBlock).HasColumnName("auto_block").HasDefaultValue(false);
            b.Property(x => x.TriggerCount).HasColumnName("trigger_count").HasDefaultValue(0);
            b.Property(x => x.LastTriggeredAt).HasColumnName("last_triggered_at").HasColumnType("timestamptz");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
        });

        // ── output_templates ───────────────────────────────────────────
        modelBuilder.Entity<OutputTemplate>(b =>
        {
            b.ToTable("output_templates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Name).HasColumnName("name").IsRequired();
            b.Property(x => x.Format).HasColumnName("format").IsRequired();
            b.Property(x => x.Version).HasColumnName("version").IsRequired();
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
        });

        // ── InvoiceEntity ─────────────────────────────────────────────────────────
        modelBuilder.Entity<InvoiceEntity>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganisationId).HasColumnName("organisation_id");
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.BuyerId).HasColumnName("buyer_id");
            e.Property(x => x.InvoiceNumber).HasColumnName("invoice_number");
            e.Property(x => x.IssueDate).HasColumnName("issue_date");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Currency).HasColumnName("currency").HasDefaultValue("EUR");
            e.Property(x => x.PaymentTerms).HasColumnName("payment_terms");
            e.Property(x => x.BuyerRef).HasColumnName("buyer_ref");
            e.Property(x => x.SupplierRef).HasColumnName("supplier_ref");
            e.Property(x => x.SubTotal).HasColumnName("sub_total").HasColumnType("numeric(18,4)");
            e.Property(x => x.TaxTotal).HasColumnName("tax_total").HasColumnType("numeric(18,4)");
            e.Property(x => x.GrandTotal).HasColumnName("grand_total").HasColumnType("numeric(18,4)");
            e.Property(x => x.Status).HasColumnName("status").HasDefaultValue("pending_review");
            e.Property(x => x.SourceFileName).HasColumnName("source_file_name");
            e.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasIndex(x => x.OrganisationId);
            e.HasOne(x => x.Organisation).WithMany()
             .HasForeignKey(x => x.OrganisationId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── InvoiceLineEntity ─────────────────────────────────────────────────────
        modelBuilder.Entity<InvoiceLineEntity>(e =>
        {
            e.ToTable("invoice_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.OrganisationId).HasColumnName("organisation_id");
            e.Property(x => x.LineNumber).HasColumnName("line_number");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(18,4)");
            e.Property(x => x.UnitCode).HasColumnName("unit_code").HasDefaultValue("EA");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18,4)");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            e.Property(x => x.LineTotal).HasColumnName("line_total").HasColumnType("numeric(18,4)");
            e.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code");
            e.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
            e.HasIndex(x => x.InvoiceId);
            e.HasOne(x => x.Invoice).WithMany(i => i.Lines)
             .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── AdvanceShippingNoticeEntity ────────────────────────────────────────────
        modelBuilder.Entity<AdvanceShippingNoticeEntity>(e =>
        {
            e.ToTable("advance_shipping_notices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganisationId).HasColumnName("organisation_id");
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.ShipmentId).HasColumnName("shipment_id");
            e.Property(x => x.DespatchDate).HasColumnName("despatch_date");
            e.Property(x => x.EstimatedDeliveryDate).HasColumnName("estimated_delivery_date");
            e.Property(x => x.BuyerOrderRef).HasColumnName("buyer_order_ref");
            e.Property(x => x.SupplierRef).HasColumnName("supplier_ref");
            e.Property(x => x.Status).HasColumnName("status").HasDefaultValue("received");
            e.Property(x => x.SourceFileName).HasColumnName("source_file_name");
            e.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasIndex(x => x.OrganisationId);
            e.HasOne(x => x.Organisation).WithMany()
             .HasForeignKey(x => x.OrganisationId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── AsnPackageEntity ───────────────────────────────────────────────────────
        modelBuilder.Entity<AsnPackageEntity>(e =>
        {
            e.ToTable("asn_packages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AdvanceShippingNoticeId).HasColumnName("advance_shipping_notice_id");
            e.Property(x => x.OrganisationId).HasColumnName("organisation_id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.Sscc).HasColumnName("sscc");
            e.HasIndex(x => x.AdvanceShippingNoticeId);
            e.HasOne(x => x.Asn).WithMany(a => a.Packages)
             .HasForeignKey(x => x.AdvanceShippingNoticeId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── AsnPackageLineEntity ───────────────────────────────────────────────────
        modelBuilder.Entity<AsnPackageLineEntity>(e =>
        {
            e.ToTable("asn_package_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.OrganisationId).HasColumnName("organisation_id");
            e.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code");
            e.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(18,4)");
            e.Property(x => x.UnitCode).HasColumnName("unit_code").HasDefaultValue("EA");
            e.HasIndex(x => x.PackageId);
            e.HasOne(x => x.Package).WithMany(p => p.Lines)
             .HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── TenantApiKey ──────────────────────────────────────────────────────────
        modelBuilder.Entity<TenantApiKey>(e =>
        {
            e.ToTable("tenant_api_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).HasColumnName("id");
            e.Property(k => k.OrganisationId).HasColumnName("organisation_id");
            e.Property(k => k.Label).HasColumnName("label");
            e.Property(k => k.KeyHash).HasColumnName("key_hash");
            e.Property(k => k.KeyPrefix).HasColumnName("key_prefix");
            e.Property(k => k.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(k => k.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(k => k.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(k => k.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.OrganisationId);
            e.HasOne(k => k.Organisation)
             .WithMany(o => o.ApiKeys)
             .HasForeignKey(k => k.OrganisationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── data_protection_keys ──────────────────────────────────────────────────
        // ASP.NET Core DataProtection key ring (auth cookies, anti-forgery tokens).
        // Persisted to Postgres so keys survive container restarts and are shared
        // across multiple API instances. Encryption at rest is handled by
        // AesGcmXmlEncryptor when DataProtection:EncryptionKey is configured.
        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.ToTable("data_protection_keys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.FriendlyName).HasColumnName("friendly_name");
            b.Property(x => x.Xml).HasColumnName("xml");
        });

        // ── IntegrationSubscription ───────────────────────────────────────────────
        modelBuilder.Entity<IntegrationSubscription>(e =>
        {
            e.ToTable("integration_subscriptions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.OrganisationId).HasColumnName("organisation_id");
            e.Property(s => s.Platform).HasColumnName("platform");
            e.Property(s => s.EventType).HasColumnName("event_type");
            e.Property(s => s.TargetUrl).HasColumnName("target_url");
            e.Property(s => s.EncryptedSecret).HasColumnName("encrypted_secret");
            e.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(s => s.FailureCount).HasColumnName("failure_count").HasDefaultValue(0);
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasIndex(s => new { s.OrganisationId, s.EventType, s.IsActive });
            e.HasOne(s => s.Organisation)
             .WithMany(o => o.IntegrationSubscriptions)
             .HasForeignKey(s => s.OrganisationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── SchemaFingerprints ────────────────────────────────────────────────────
        // The physical table + column names are intentionally kept PascalCase (the EF
        // default the original migration created) rather than renamed to snake_case like
        // the rest of the schema. This keeps the migration purely additive — a single
        // CreateIndex with no RenameTable/RenameColumn — so it is safe on already-deployed
        // databases. Tradeoff: this one table diverges from the repo snake_case convention;
        // a deliberate rename can be coordinated later if convention alignment is wanted.
        //
        // The unique index on (OrganisationId, ColumnNameHash) is the actual fix: it enforces
        // one fingerprint row per org+layout at the database level. Without it, two concurrent
        // ParseOrderJob workers for different orders with the same column layout both INSERT,
        // silently duplicating fingerprints and undercounting ParseSuccessCount — and the
        // concurrent-insert race-recovery path in SchemaFingerprintService (which catches the
        // Postgres 23505 unique violation) is dead code. The index leads with OrganisationId,
        // so the org-scoped lookup/upsert queries stay index-aligned.
        modelBuilder.Entity<SchemaFingerprint>(b =>
        {
            b.ToTable("SchemaFingerprints");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OrganisationId, x.ColumnNameHash })
             .IsUnique()
             .HasDatabaseName("IX_schema_fingerprints_org_id_column_name_hash");
        });

        // ── order_confirmations ────────────────────────────────────────────────────
        // Supplier acknowledgements of a purchase order (inbound counterpart to the PO).
        modelBuilder.Entity<OrderConfirmationEntity>(b =>
        {
            b.ToTable("order_confirmations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id");
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.SupplierReference).HasColumnName("supplier_reference");
            b.Property(x => x.Source).HasColumnName("source").IsRequired();
            b.Property(x => x.SourceFileName).HasColumnName("source_file_name");
            b.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
            b.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            // (OrgId, PurchaseOrderId): list confirmations for an order; completion-blocking check.
            b.HasIndex(x => new { x.OrgId, x.PurchaseOrderId })
             .HasDatabaseName("IX_order_confirmations_org_id_purchase_order_id");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.PurchaseOrder)
             .WithMany()
             .HasForeignKey(x => x.PurchaseOrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── order_confirmation_lines ───────────────────────────────────────────────
        modelBuilder.Entity<OrderConfirmationLineEntity>(b =>
        {
            b.ToTable("order_confirmation_lines");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderConfirmationId).HasColumnName("order_confirmation_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.PurchaseOrderLineId).HasColumnName("purchase_order_line_id");
            b.Property(x => x.LineNumber).HasColumnName("line_number");
            b.Property(x => x.BuyerItemCode).HasColumnName("buyer_item_code");
            b.Property(x => x.SupplierItemCode).HasColumnName("supplier_item_code");
            b.Property(x => x.OrderedQuantity).HasColumnName("ordered_quantity").HasColumnType("numeric(18,4)");
            b.Property(x => x.OrderedUnitPrice).HasColumnName("ordered_unit_price").HasColumnType("numeric(18,4)");
            b.Property(x => x.OrderedDeliveryDate).HasColumnName("ordered_delivery_date");
            b.Property(x => x.ConfirmedQuantity).HasColumnName("confirmed_quantity").HasColumnType("numeric(18,4)");
            b.Property(x => x.ConfirmedUnitPrice).HasColumnName("confirmed_unit_price").HasColumnType("numeric(18,4)");
            b.Property(x => x.ConfirmedDeliveryDate).HasColumnName("confirmed_delivery_date");
            b.Property(x => x.State).HasColumnName("state").IsRequired();
            b.Property(x => x.Note).HasColumnName("note");
            b.HasIndex(x => x.OrderConfirmationId)
             .HasDatabaseName("IX_order_confirmation_lines_order_confirmation_id");
            b.HasOne(x => x.OrderConfirmation)
             .WithMany(c => c.Lines)
             .HasForeignKey(x => x.OrderConfirmationId)
             .OnDelete(DeleteBehavior.Cascade);
            // Reference the ordered PO line without a cascade: deleting a PO line should not
            // silently delete confirmation history. Optional FK (extra/unknown lines allowed).
            b.HasOne(x => x.PurchaseOrderLine)
             .WithMany()
             .HasForeignKey(x => x.PurchaseOrderLineId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── mapping_corrections ────────────────────────────────────────
        modelBuilder.Entity<MappingCorrection>(b =>
        {
            b.ToTable("mapping_corrections");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.MappingId).HasColumnName("mapping_id");
            b.Property(x => x.OldSupplierItemCode).HasColumnName("old_supplier_item_code").IsRequired();
            b.Property(x => x.NewSupplierItemCode).HasColumnName("new_supplier_item_code").IsRequired();
            b.Property(x => x.Source).HasColumnName("source").IsRequired();
            b.Property(x => x.CorrectedAt)
             .HasColumnName("corrected_at")
             .HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Mapping).WithMany().HasForeignKey(x => x.MappingId);
            b.HasIndex(x => new { x.OrgId, x.MappingId, x.CorrectedAt })
             .HasDatabaseName("IX_mapping_corrections_org_id_mapping_id_corrected_at");
        });

        // ── po_passport_events ──────────────────────────────────────────
        modelBuilder.Entity<PoPassportEvent>(b =>
        {
            b.ToTable("po_passport_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.Stage).HasColumnName("stage").IsRequired();
            b.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            b.Property(x => x.ActorType).HasColumnName("actor_type").IsRequired();
            b.Property(x => x.ActorId).HasColumnName("actor_id");
            b.Property(x => x.Payload)
             .HasColumnName("payload")
             .HasColumnType("jsonb");
            b.Property(x => x.OccurredAt)
             .HasColumnName("occurred_at")
             .HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.OccurredAt })
             .HasDatabaseName("IX_po_passport_events_org_id_order_id_occurred_at");
        });
    }

    private static JsonDocument? ParseJsonDoc(string? v) =>
        v == null ? null : JsonDocument.Parse(v);
}
