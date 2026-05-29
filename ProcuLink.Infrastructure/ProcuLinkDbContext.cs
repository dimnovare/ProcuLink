using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    public DbSet<InvoiceEntity>                Invoices                  => Set<InvoiceEntity>();
    public DbSet<InvoiceLineEntity>            InvoiceLines              => Set<InvoiceLineEntity>();
    public DbSet<AdvanceShippingNoticeEntity>  AdvanceShippingNotices    => Set<AdvanceShippingNoticeEntity>();
    public DbSet<AsnPackageEntity>             AsnPackages               => Set<AsnPackageEntity>();
    public DbSet<AsnPackageLineEntity>         AsnPackageLines           => Set<AsnPackageLineEntity>();
    public DbSet<TenantApiKey>            TenantApiKeys            => Set<TenantApiKey>();
    public DbSet<IntegrationSubscription> IntegrationSubscriptions => Set<IntegrationSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            b.Property(x => x.Slug)
             .HasColumnName("slug")
             .HasDefaultValue("")
             .IsRequired();
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.WebhookSecretEncrypted)
             .HasColumnName("webhook_secret_encrypted")
             .HasColumnType("text");
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
             .HasColumnType("jsonb")
             .HasConversion(jsonDocConverter);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            b.Property(x => x.IsSample).HasColumnName("is_sample").HasDefaultValue(false);
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
    }

    private static JsonDocument? ParseJsonDoc(string? v) =>
        v == null ? null : JsonDocument.Parse(v);
}
