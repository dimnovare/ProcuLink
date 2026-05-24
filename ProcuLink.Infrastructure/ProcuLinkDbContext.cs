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
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.HasIndex(x => x.ClerkOrgId).IsUnique();
            b.Property(x => x.TrialStartedAt)
             .HasColumnName("trial_started_at")
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
             .HasForeignKey(x => x.OrderId);
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.DeliveryAttempts)
             .HasForeignKey(x => x.OrgId);
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
    }
}
