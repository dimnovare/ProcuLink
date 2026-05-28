using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure;

public class ProcuLinkDbContext : DbContext
{
    public ProcuLinkDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierProfileEntity> SupplierProfiles => Set<SupplierProfileEntity>();
    public DbSet<PurchaseOrderEntity> PurchaseOrders => Set<PurchaseOrderEntity>();
    public DbSet<PurchaseOrderLineEntity> PurchaseOrderLines => Set<PurchaseOrderLineEntity>();
    public DbSet<ItemMapping> ItemMappings => Set<ItemMapping>();
    public DbSet<OutboundArtifact> OutboundArtifacts => Set<OutboundArtifact>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupplierPoMapping> SupplierPoMappings => Set<SupplierPoMapping>();
    public DbSet<SupplierDeliveryConfig> SupplierDeliveryConfigs => Set<SupplierDeliveryConfig>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<AiUsageMonthly> AiUsageMonthly => Set<AiUsageMonthly>();
    public DbSet<SftpIngressConfig> SftpIngressConfigs => Set<SftpIngressConfig>();
    public DbSet<ImportedSftpFile> ImportedSftpFiles => Set<ImportedSftpFile>();
    public DbSet<S3IngressConfig> S3IngressConfigs => Set<S3IngressConfig>();
    public DbSet<ImportedS3Object> ImportedS3Objects => Set<ImportedS3Object>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<ValidationRule> ValidationRules => Set<ValidationRule>();
    public DbSet<OutputTemplate> OutputTemplates => Set<OutputTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
             .HasColumnType("jsonb");
            b.Property(x => x.OutputFormat).HasColumnName("output_format").IsRequired();
            b.Property(x => x.DestinationType).HasColumnName("destination_type").IsRequired();
            b.Property(x => x.DestinationConfig)
             .HasColumnName("destination_config")
             .HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.SupplierProfiles)
             .HasForeignKey(x => x.SupplierId);
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
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
            b.Property(x => x.OrderDate).HasColumnName("order_date");
            b.Property(x => x.Currency).HasColumnName("currency").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").IsRequired();
            b.Property(x => x.SourceFileKey).HasColumnName("source_file_key");
            b.Property(x => x.CanonicalJson)
             .HasColumnName("canonical_json")
             .HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
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
            b.HasOne(x => x.Order)
             .WithMany(x => x.Lines)
             .HasForeignKey(x => x.OrderId);
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
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.BuyerItemCode }).IsUnique();
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.ItemMappings)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.ItemMappings)
             .HasForeignKey(x => x.SupplierId);
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
            b.HasOne(x => x.Order)
             .WithMany(x => x.DeliveryAttempts)
             .HasForeignKey(x => x.OrderId)
             .IsRequired(false);
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.DeliveryAttempts)
             .HasForeignKey(x => x.OrgId);
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
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne<Organisation>()
             .WithMany()
             .HasForeignKey(x => x.OrgId);
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
            b.Property(x => x.AccessKeyId).HasColumnName("access_key_id").IsRequired();
            b.Property(x => x.EncryptedSecretKey).HasColumnName("encrypted_secret_key").IsRequired();
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(false);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne<Organisation>()
             .WithMany()
             .HasForeignKey(x => x.OrgId);
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
             .HasColumnType("jsonb");
            b.Property(x => x.CreatedAt)
             .HasColumnName("created_at")
             .HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.AuditEvents)
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.User)
             .WithMany(x => x.AuditEvents)
             .HasForeignKey(x => x.UserId);
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
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
        });
    }
}
