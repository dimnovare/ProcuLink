using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Infrastructure;

public class ProcuLinkDbContext : DbContext, IDataProtectionKeyContext
{
    public ProcuLinkDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

    // ── Organisation query filters ───────────────────────────────────────────
    // Global query filters make org scoping the model's default (see
    // Tenancy/OrgQueryFilters.cs). The filter reads ScopedOrganisationId at query time, so a
    // context is UNSCOPED until something arms it.
    //
    // Unscoped is the default deliberately, and it is the behaviour this codebase had before the
    // filters existed: the ~320 explicit `.Where(x => x.OrgId == orgId)` clauses remain and are
    // still the active control. Defaulting to ARMED would have been wrong in both directions —
    //
    //   * Fail-closed-silently is the worse failure. A cross-org sweep that suddenly matches no
    //     rows looks like a clean run, not an outage. The Worker runs 13 such sweeps (billing
    //     reconciliation, stuck/stranded/SLA detection, IMAP+SFTP+S3 polling, retention, alerting)
    //     and every one of them would have reported success over an empty set.
    //
    //   * Several API paths legitimately read across organisations WITH a tenant resolved —
    //     AdminController's cross-org aggregates over PurchaseOrders and Suppliers, and
    //     OpsHealthService's cross-tenant health counts behind OpsController. Arming the filter
    //     for every request would silently truncate those to the caller's own org.
    //
    // Arming is therefore explicit and greppable at the call site, never ambient: grep for
    // ScopeToOrganisation( and UseCrossOrganisationScope( to see every decision.
    private Guid? _scopedOrgId;

    /// <summary>
    /// The organisation this context is scoped to, or null when it is unscoped and the
    /// organisation query filters are inert. Read by the filter predicate on every query.
    /// </summary>
    public Guid? ScopedOrganisationId => _scopedOrgId;

    /// <summary>
    /// The reason recorded by the most recent <see cref="UseCrossOrganisationScope"/> call, or
    /// null. Carried for diagnostics so an unscoped context can say why it is unscoped.
    /// </summary>
    public string? CrossOrganisationReason { get; private set; }

    /// <summary>
    /// Arms the organisation query filters: from this point every query against an org-scoped
    /// entity is restricted to <paramref name="orgId"/>, whether or not the caller wrote an
    /// explicit <c>.Where</c> clause.
    /// </summary>
    /// <remarks>
    /// Safe to call repeatedly with the same id. Changing the scope mid-context is refused rather
    /// than silently honoured, because entities already tracked from the previous organisation
    /// would stay in the change tracker and be returned from tracked queries without ever going
    /// back to the database — a filter bypass that would look like a normal read.
    /// </remarks>
    public ProcuLinkDbContext ScopeToOrganisation(Guid orgId)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException(
                "Cannot scope a DbContext to Guid.Empty — an empty organisation id would match " +
                "no rows and read as a clean, empty result rather than as the error it is.",
                nameof(orgId));

        if (_scopedOrgId is { } existing && existing != orgId)
            throw new InvalidOperationException(
                $"This DbContext is already scoped to organisation {existing} and cannot be " +
                $"re-scoped to {orgId}. Entities tracked under the previous scope would survive " +
                "in the change tracker and be served from it. Use a new DbContext (or a new DI " +
                "scope) per organisation.");

        _scopedOrgId = orgId;
        CrossOrganisationReason = null;

        // Resolve the model NOW, with the scope already set, so this context binds to the FILTERED
        // model (see OrgScopeModelCacheKeyFactory). The model is resolved once per instance, so a
        // context that has already run a query is stuck on the unfiltered model — and would then
        // read every organisation's rows while looking, at the call site, exactly like a scoped
        // one. That is the silent fail-open this check exists to make loud.
        if (Model.FindEntityType(typeof(PurchaseOrderEntity))?.GetQueryFilter() is null)
            throw new InvalidOperationException(
                "ScopeToOrganisation was called after this DbContext had already resolved its " +
                "model, so the organisation query filters are NOT applied to it and every query " +
                "would read across tenants. Scope the context before its first query — typically " +
                "immediately after it is resolved from DI.");

        return this;
    }

    /// <summary>
    /// Installs the model-cache split that gives scoped and unscoped contexts different models.
    /// Done here rather than at the AddDbContext call sites so both hosts (API and Worker) and
    /// every test that news up a context get it without a registration each.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, OrgScopeModelCacheKeyFactory>();
    }

    /// <summary>
    /// Declares that this context intentionally reads across organisations, leaving the
    /// organisation query filters inert.
    /// </summary>
    /// <remarks>
    /// This does not change behaviour — an unscoped context is already unfiltered — but it makes
    /// the intent explicit and greppable at the call site instead of relying on the reader to
    /// notice that nothing armed the scope. Cross-organisation work must still scope its own
    /// writes; the filter never protected those.
    /// </remarks>
    public ProcuLinkDbContext UseCrossOrganisationScope(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A cross-organisation scope must state why it reads across tenants.",
                nameof(reason));

        if (_scopedOrgId is { } existing)
            throw new InvalidOperationException(
                $"This DbContext is scoped to organisation {existing}; it cannot also declare a " +
                "cross-organisation scope. Use a separate DbContext for cross-tenant work.");

        CrossOrganisationReason = reason;
        return this;
    }

    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierProfileEntity> SupplierProfiles => Set<SupplierProfileEntity>();
    public DbSet<PurchaseOrderEntity> PurchaseOrders => Set<PurchaseOrderEntity>();
    public DbSet<PurchaseOrderLineEntity> PurchaseOrderLines => Set<PurchaseOrderLineEntity>();
    public DbSet<OrderParty> OrderParties => Set<OrderParty>();
    public DbSet<SourceCapture> SourceCaptures => Set<SourceCapture>();
    public DbSet<ItemMapping> ItemMappings => Set<ItemMapping>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<SupplierCatalogSource> SupplierCatalogSources => Set<SupplierCatalogSource>();
    public DbSet<AiSuggestionDecision> AiSuggestionDecisions => Set<AiSuggestionDecision>();
    public DbSet<OutboundArtifact> OutboundArtifacts => Set<OutboundArtifact>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AutoSendDryRun> AutoSendDryRuns => Set<AutoSendDryRun>();
    public DbSet<SupplierPoMapping> SupplierPoMappings => Set<SupplierPoMapping>();
    public DbSet<SupplierDeliveryConfig> SupplierDeliveryConfigs => Set<SupplierDeliveryConfig>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<AiUsageMonthly> AiUsageMonthly => Set<AiUsageMonthly>();
    public DbSet<OverageBillingRecord> OverageBillingRecords => Set<OverageBillingRecord>();
    public DbSet<SftpIngressConfig> SftpIngressConfigs => Set<SftpIngressConfig>();
    public DbSet<ImportedSftpFile> ImportedSftpFiles => Set<ImportedSftpFile>();
    public DbSet<S3IngressConfig> S3IngressConfigs => Set<S3IngressConfig>();
    public DbSet<ImportedS3Object> ImportedS3Objects => Set<ImportedS3Object>();
    public DbSet<EmailImportRecord> EmailImportRecords => Set<EmailImportRecord>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
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
    // ── Group V4: unified validation — reusable rule definitions (templates) ─
    public DbSet<RuleDefinition>              RuleDefinitions              { get; set; } = null!;
    // ── Group V1: versioned Supplier Connection ─────────────────────────────
    public DbSet<SupplierConnection>             SupplierConnections           { get; set; } = null!;
    public DbSet<SupplierConnectionRevision>     SupplierConnectionRevisions   { get; set; } = null!;
    public DbSet<ConnectionRevisionItemMapping>  ConnectionRevisionItemMappings { get; set; } = null!;
    public DbSet<ConnectionRevisionTestCase>     ConnectionRevisionTestCases   { get; set; } = null!;
    // ── Phase 2: extensible canonical — user-defined spine fields (Tier-2) ───
    public DbSet<CanonicalFieldDef>              CanonicalFieldDefs            { get; set; } = null!;
    // ── Billing: append-only plan/override history (as-of overage metering) ──
    public DbSet<OrgPlanHistory>                 OrgPlanHistories              { get; set; } = null!;
    // ── Data retention: append-only evidence trail of the blob-retention sweep ─
    public DbSet<RetentionAuditLog>              RetentionAuditLogs            { get; set; } = null!;
    // ── Supplier auto-detect: ranked candidates for an order that arrived unrouted ─
    public DbSet<OrderSupplierSuggestion>        OrderSupplierSuggestions      { get; set; } = null!;

    // ── Org plan-history chokepoint ──────────────────────────────────────────
    // Overage metering must resolve the plan + order-limit override AS OF each
    // billed window (a yearly renewal invoice decomposes into ~12 PAST months;
    // metering them with today's plan retroactively re-prices history). Rather
    // than instrumenting every write point (Stripe webhook plan mapping ×3,
    // checkout completion, admin limits endpoint, MarkPilotStartedAsync, and any
    // future one), SaveChanges is the single chokepoint: whenever a TRACKED
    // Organisation is inserted, or its Plan / OrderLimitOverride is modified, a
    // history row is appended in the SAME save (atomic with the org mutation).
    // NOTE: this covers tracked-entity writes only — there are no
    // ExecuteUpdate/raw-SQL writers of these two columns (and none may be added
    // without writing history).
    //
    // Test-model guard: several test harnesses subclass this context with a
    // reduced model that does not map OrgPlanHistory; the hook no-ops there.
    // The production model always maps it, so this guard can never skip a real
    // write.

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AppendOrgPlanHistoryAsync(useAsync: false).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await AppendOrgPlanHistoryAsync(useAsync: true, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task AppendOrgPlanHistoryAsync(bool useAsync, CancellationToken ct = default)
    {
        // Reduced test models (and only those) may not map the history entity.
        if (Model.FindEntityType(typeof(OrgPlanHistory)) is null) return;

        // Snapshot the entries first — we mutate the change tracker below.
        var orgEntries = ChangeTracker.Entries<Organisation>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        if (orgEntries.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in orgEntries)
        {
            var org = entry.Entity;

            if (entry.State == EntityState.Added)
            {
                // New org: one baseline row so every later window resolves as-of.
                // CreatedAt may still be unset when the DB generates it; fall back to now.
                OrgPlanHistories.Add(NewRow(org.Id, org.Plan, org.OrderLimitOverride,
                    org.CreatedAt == default ? now : new DateTimeOffset(EnsureUtc(org.CreatedAt))));
                continue;
            }

            var planProp     = entry.Property(nameof(Organisation.Plan));
            var overrideProp = entry.Property(nameof(Organisation.OrderLimitOverride));
            var planChanged     = !string.Equals((string?)planProp.OriginalValue, org.Plan, StringComparison.Ordinal);
            var overrideChanged = (int?)overrideProp.OriginalValue != org.OrderLimitOverride;
            if (!planChanged && !overrideChanged) continue;

            // Self-healing baseline: an org that predates the history table (boot
            // seed not yet run, or created before this feature) gets its OLD values
            // recorded at org creation, so the windows BEFORE this change still
            // resolve to the pre-change plan/override instead of falling back.
            //
            // STRUCTURAL INVARIANT (pinned by OrgPlanHistoryInvariantTests): when a
            // baseline is healed in alongside a change, the baseline's EffectiveFrom is
            // strictly BEFORE the change row's. The as-of metering reader
            // (StripeBillingService.ComputePeriodOverageOrdersAsync) picks the latest
            // row with EffectiveFrom ≤ windowStart; if a healed baseline tied the change
            // row at the same instant, that pick would be ambiguous (plan/override could
            // flip between meterings of the same window). The 1 ms guard below keeps the
            // two strictly ordered even in the degenerate CreatedAt==default case.
            var hasHistory = OrgPlanHistories.Local.Any(h => h.OrgId == org.Id)
                || (useAsync
                    ? await OrgPlanHistories.AsNoTracking().AnyAsync(h => h.OrgId == org.Id, ct)
                    : OrgPlanHistories.AsNoTracking().Any(h => h.OrgId == org.Id));
            if (!hasHistory)
            {
                // Baseline strictly BEFORE the change row (1 ms guard for the
                // degenerate CreatedAt==default case) so as-of resolution can
                // never tie the old and new values at the same instant.
                var baselineFrom = org.CreatedAt == default
                    ? now.AddMilliseconds(-1)
                    : new DateTimeOffset(EnsureUtc(org.CreatedAt));
                OrgPlanHistories.Add(NewRow(
                    org.Id,
                    (string?)planProp.OriginalValue ?? org.Plan,
                    (int?)overrideProp.OriginalValue,
                    baselineFrom));
            }

            OrgPlanHistories.Add(NewRow(org.Id, org.Plan, org.OrderLimitOverride, now));
        }

        static OrgPlanHistory NewRow(Guid orgId, string plan, int? orderLimitOverride, DateTimeOffset effectiveFrom) =>
            new()
            {
                Id                 = Guid.NewGuid(),
                OrgId              = orgId,
                Plan               = plan,
                OrderLimitOverride = orderLimitOverride,
                EffectiveFrom      = effectiveFrom,
            };

        static DateTime EnsureUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

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
            b.Property(x => x.StripeReconciliationMissingSince)
             .HasColumnName("stripe_reconciliation_missing_since")
             .HasColumnType("timestamptz");
            b.Property(x => x.LastStripeEventAt)
             .HasColumnName("last_stripe_event_at")
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
            b.Property(x => x.SelfHostedOcr)
             .HasColumnName("self_hosted_ocr")
             .HasDefaultValue(false);
            // Blob retention: NULL (default) = retention DISABLED for this org.
            b.Property(x => x.RetentionDays)
             .HasColumnName("retention_days");
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
            // Identity columns (D1) — the right-hand side supplier auto-detect had no way to
            // compare a document's VAT / registry code / EDI address / sender domain against.
            // All nullable: an org that fills none of them in simply loses those signals.
            b.Property(x => x.VatNumber).HasColumnName("vat_number");
            b.Property(x => x.RegistrationNumber).HasColumnName("registration_number");
            b.Property(x => x.EdiCode).HasColumnName("edi_code");
            b.Property(x => x.PrimaryDomain).HasColumnName("primary_domain");
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
            b.Property(x => x.AutoTransform).HasColumnName("auto_transform").HasDefaultValue(false).ValueGeneratedNever();
            b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            b.Property(x => x.EncryptedCredentials).HasColumnName("encrypted_credentials").IsRequired();
            b.Property(x => x.OutputFormat).HasColumnName("output_format");
            b.Property(x => x.CxmlConfigJson).HasColumnName("cxml_config_json").HasColumnType("jsonb");
            b.Property(x => x.EncryptedCxmlSharedSecret).HasColumnName("encrypted_cxml_shared_secret");
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
            // Delivery-phase re-drive counter — SEPARATE budget from requeue_count so a parse/transform
            // requeue never eats into the delivery stuck-recovery budget (StuckDeliveryDetectionService).
            b.Property(x => x.DeliveryRequeueCount).HasColumnName("delivery_requeue_count").HasDefaultValue(0);
            // Inbound sender DOMAIN only (D2) — never the local part, never the full address,
            // which stays SHA-256-only in the audit payload. Scrubbed by the data-retention sweep
            // 12 months after capture; the capture timestamp is its own column so the retention
            // clock cannot be reset by an unrelated write to the order.
            b.Property(x => x.InboundSenderDomain).HasColumnName("inbound_sender_domain");
            b.Property(x => x.InboundSenderDomainCapturedAt)
             .HasColumnName("inbound_sender_domain_captured_at").HasColumnType("timestamptz");
            // Phase 4 enrichment + doc-type classification (nullable).
            b.Property(x => x.SupplierName).HasColumnName("supplier_name");
            b.Property(x => x.SubTotal).HasColumnName("sub_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.TaxTotal).HasColumnName("tax_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.GrandTotal).HasColumnName("grand_total").HasColumnType("numeric(18,4)");
            b.Property(x => x.PaymentTerms).HasColumnName("payment_terms");
            b.Property(x => x.DocumentType).HasColumnName("document_type");
            // Group V1: the connection revision this order was pinned to at ingest (nullable; legacy = null).
            b.Property(x => x.ConnectionRevisionId).HasColumnName("connection_revision_id");
            // V5 deepen-canonical: real persisted nullable date column (mirrors per-line delivery_date).
            // Migration AddRequestedDeliveryDate. Null for formats with no header-level delivery date.
            b.Property(x => x.RequestedDeliveryDate).HasColumnName("requested_delivery_date");
            // Phase 1 lossless capture (nullable additive columns).
            b.Property(x => x.ContactName).HasColumnName("contact_name");
            b.Property(x => x.ContactEmail).HasColumnName("contact_email");
            b.Property(x => x.ContactPhone).HasColumnName("contact_phone");
            b.Property(x => x.Incoterms).HasColumnName("incoterms");
            b.Property(x => x.ShippingMethod).HasColumnName("shipping_method");
            b.Property(x => x.BuyerOrderRef).HasColumnName("buyer_order_ref");
            // Buyer tax id (nullable; feeds the cXML From/Identity). Migration AddBuyerTaxIdAndLineTax.
            b.Property(x => x.BuyerTaxId).HasColumnName("buyer_tax_id");
            // cXML address blocks (nullable; denormalised from shipTo/billTo OrderParty rows).
            b.Property(x => x.ShipToName).HasColumnName("ship_to_name");
            b.Property(x => x.ShipToDeliverTo).HasColumnName("ship_to_deliver_to");
            b.Property(x => x.ShipToStreet).HasColumnName("ship_to_street");
            b.Property(x => x.ShipToCity).HasColumnName("ship_to_city");
            b.Property(x => x.ShipToPostalCode).HasColumnName("ship_to_postal_code");
            b.Property(x => x.ShipToCountry).HasColumnName("ship_to_country");
            b.Property(x => x.ShipToEmail).HasColumnName("ship_to_email");
            b.Property(x => x.ShipToPhone).HasColumnName("ship_to_phone");
            b.Property(x => x.BillToName).HasColumnName("bill_to_name");
            b.Property(x => x.BillToDeliverTo).HasColumnName("bill_to_deliver_to");
            b.Property(x => x.BillToStreet).HasColumnName("bill_to_street");
            b.Property(x => x.BillToCity).HasColumnName("bill_to_city");
            b.Property(x => x.BillToPostalCode).HasColumnName("bill_to_postal_code");
            b.Property(x => x.BillToCountry).HasColumnName("bill_to_country");
            b.Property(x => x.BillToEmail).HasColumnName("bill_to_email");
            b.Property(x => x.BillToPhone).HasColumnName("bill_to_phone");
            // Blob retention: when the source-file blob was purged from R2 (row + key stay).
            b.Property(x => x.SourceFilePurgedAt)
             .HasColumnName("source_file_purged_at")
             .HasColumnType("timestamptz");
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
            // (OrgId, InboundSenderDomain): supplier auto-detect's sender-domain history probe —
            // "which suppliers did this org's earlier orders from this domain get routed to?".
            // Also the retention scrub's selection predicate (domain IS NOT NULL, past the window).
            b.HasIndex(x => new { x.OrgId, x.InboundSenderDomain })
             .HasDatabaseName("IX_purchase_orders_org_id_inbound_sender_domain");
            b.HasOne(x => x.Organisation)
             .WithMany(x => x.PurchaseOrders)
             .HasForeignKey(x => x.OrgId);
            // SupplierId is nullable (Phase 0 routing): an unrouted order has no supplier yet, and
            // deleting a supplier nulls the FK rather than cascade-deleting the order's history.
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.PurchaseOrders)
             .HasForeignKey(x => x.SupplierId)
             .OnDelete(DeleteBehavior.SetNull);
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
            // P2 hardening: short "why was this flagged" string written at parse time (nullable; additive).
            b.Property(x => x.ReviewReason).HasColumnName("review_reason");
            b.Property(x => x.AiSuggestedSupplierItemCode).HasColumnName("ai_suggested_supplier_item_code");
            b.Property(x => x.AiSuggestionConfidence).HasColumnName("ai_suggestion_confidence");
            b.Property(x => x.AiSuggestionReason).HasColumnName("ai_suggestion_reason");
            b.Property(x => x.AiSuggestionProvenance).HasColumnName("ai_suggestion_provenance");
            // Phase 4 enrichment (nullable).
            b.Property(x => x.LineAmount).HasColumnName("line_amount").HasColumnType("numeric(18,4)");
            b.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            // Per-line VAT amount (nullable). Migration AddBuyerTaxIdAndLineTax.
            b.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasColumnType("numeric(18,4)");
            b.Property(x => x.DeliveryDate).HasColumnName("delivery_date");
            // Phase 1 lossless capture (nullable additive columns).
            b.Property(x => x.ManufacturerPartNumber).HasColumnName("manufacturer_part_number");
            b.Property(x => x.ManufacturerName).HasColumnName("manufacturer_name");
            b.Property(x => x.CustomerPartNumber).HasColumnName("customer_part_number");
            b.Property(x => x.DiscountPercent).HasColumnName("discount_percent").HasColumnType("numeric(7,4)");
            b.Property(x => x.Unspsc).HasColumnName("unspsc");
            b.Property(x => x.Recipient).HasColumnName("recipient");
            b.Property(x => x.ContractNumber).HasColumnName("contract_number");
            b.Property(x => x.NetAmount).HasColumnName("net_amount").HasColumnType("numeric(18,4)");
            b.HasOne(x => x.Order)
             .WithMany(x => x.Lines)
             .HasForeignKey(x => x.OrderId);
            b.HasIndex(x => new { x.OrderId, x.NeedsReview })
             .HasDatabaseName("IX_purchase_order_lines_order_id_needs_review");
        });

        // ── order_parties (Phase 1 lossless capture) ───────────────────
        modelBuilder.Entity<OrderParty>(b =>
        {
            b.ToTable("order_parties");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Role).HasColumnName("role").IsRequired();
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Street).HasColumnName("street");
            b.Property(x => x.City).HasColumnName("city");
            b.Property(x => x.PostalCode).HasColumnName("postal_code");
            b.Property(x => x.Country).HasColumnName("country");
            b.Property(x => x.Vat).HasColumnName("vat");
            b.Property(x => x.RegNr).HasColumnName("reg_nr");
            b.Property(x => x.EdiCode).HasColumnName("edi_code");
            b.Property(x => x.Reference).HasColumnName("reference");
            b.Property(x => x.ContactName).HasColumnName("contact_name");
            b.Property(x => x.Email).HasColumnName("email");
            b.Property(x => x.Phone).HasColumnName("phone");
            b.HasOne(x => x.Order).WithMany(x => x.Parties).HasForeignKey(x => x.OrderId);
            b.HasIndex(x => new { x.OrgId, x.OrderId }).HasDatabaseName("IX_order_parties_org_id_order_id");
        });

        // ── source_captures (Phase 1 lossless raw bag) ─────────────────
        modelBuilder.Entity<SourceCapture>(b =>
        {
            b.ToTable("source_captures");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Format).HasColumnName("format").IsRequired();
            b.Property(x => x.CapturedAt).HasColumnName("captured_at").HasColumnType("timestamptz");
            b.Property(x => x.TokensJson).HasColumnName("tokens_json").HasColumnType("jsonb").HasConversion(jsonDocConverter);
            b.Property(x => x.RawText).HasColumnName("raw_text");
            b.Property(x => x.PageRefs).HasColumnName("page_refs");
            b.HasOne(x => x.Order).WithOne(x => x.SourceCapture).HasForeignKey<SourceCapture>(x => x.OrderId);
            b.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("IX_source_captures_order_id");
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
            b.Property(x => x.ManufacturerPartNumber).HasColumnName("manufacturer_part_number");
            b.Property(x => x.ManufacturerPartNumberNormalized)
             .HasColumnName("manufacturer_part_number_normalized");
            b.Property(x => x.ManufacturerName).HasColumnName("manufacturer_name");
            b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // One row per real code per (org, supplier) — the upsert key. Also serves the
            // V10 indexed EXACT code lookup (org_id, supplier_id, code) without a second btree.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Code }).IsUnique();
            // Supplier auto-detect asks the one question the unique index above cannot answer:
            // "WHICH suppliers sell these codes?" — org-scoped with supplier_id unconstrained.
            // Against the (org, supplier, code) index that degenerates into scanning the org's
            // entire slice, which on a 200k-row catalog is the wrong shape. Leading (org, code)
            // makes it a direct probe.
            b.HasIndex(x => new { x.OrgId, x.Code })
             .HasDatabaseName("IX_supplier_products_org_id_code");
            // Active-catalog listing / typeahead read path.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.IsActive });
            // V10 — indexed EXACT barcode (GTIN/EAN) lookup, the strongest match key when
            // buyers carry it. Btree on (org, supplier, barcode). The GIN trigram indexes on
            // code+name (for the fuzzy ranking pass) are added in the migration via raw SQL
            // because EF model config cannot express the gin_trgm_ops operator class.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.Barcode })
             .HasDatabaseName("IX_supplier_products_org_id_supplier_id_barcode");
            // Manufacturer-part-number fallback lookup: when a line's supplier code resolves
            // against nothing (punchout orders carry the buying network's internal id), the
            // manufacturer part number is the only usable key. Btree on the NORMALISED column
            // so the query is a plain equality — never a function over every row. Deliberately
            // NOT unique: one manufacturer part legitimately appears under several supplier
            // codes (kit vs bare unit, regional variants), and the resolver treats that
            // multiplicity as "ambiguous, suggest nothing" rather than picking one.
            b.HasIndex(x => new { x.OrgId, x.SupplierId, x.ManufacturerPartNumberNormalized })
             .HasDatabaseName("IX_supplier_products_org_id_supplier_id_mpn_normalized");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany(x => x.Products)
             .HasForeignKey(x => x.SupplierId);
        });

        // ── supplier_catalog_sources ───────────────────────────────────
        // Pull-sync config for one supplier's catalog (SFTP/FTP/FTPS). One source per
        // (org, supplier) — the unique index doubles as the upsert key. Conventions
        // mirror supplier_products: org+supplier scoped, snake_case, explicit
        // HasColumnName on every property (the migration relies on these).
        modelBuilder.Entity<SupplierCatalogSource>(b =>
        {
            b.ToTable("supplier_catalog_sources");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.Protocol).HasColumnName("protocol").IsRequired();
            b.Property(x => x.Host).HasColumnName("host").IsRequired();
            b.Property(x => x.Port).HasColumnName("port");
            b.Property(x => x.Username).HasColumnName("username");
            b.Property(x => x.EncryptedPassword).HasColumnName("encrypted_password");
            b.Property(x => x.RemotePath).HasColumnName("remote_path").IsRequired();
            // Trusted SSH host-key fingerprint(s) for sftp sources (WP-38). Nullable cleartext text:
            // the digest of a PUBLIC key, so unlike encrypted_password it is meant to be read back.
            b.Property(x => x.HostKeyFingerprints).HasColumnName("host_key_fingerprints");
            // HTTP(S) pull columns (plan 2026-06-12 v2). All nullable — sftp/ftp rows leave them null.
            b.Property(x => x.Url).HasColumnName("url");
            b.Property(x => x.AuthMethod).HasColumnName("auth_method");
            b.Property(x => x.AuthConfigEncrypted).HasColumnName("auth_config_encrypted");
            b.Property(x => x.HttpMethod).HasColumnName("http_method").HasDefaultValue("GET");
            b.Property(x => x.FileFormat).HasColumnName("file_format").IsRequired().HasDefaultValue("auto");
            // Per-source column mapping (plan 2026-07-02 D3). Nullable text — flat JSON object.
            b.Property(x => x.ColumnMappingJson).HasColumnName("column_mapping_json");
            b.Property(x => x.SyncIntervalHours).HasColumnName("sync_interval_hours").HasDefaultValue(24);
            b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(false);
            b.Property(x => x.LastSyncAt).HasColumnName("last_sync_at");
            b.Property(x => x.LastSyncStatus).HasColumnName("last_sync_status");
            b.Property(x => x.LastSyncError).HasColumnName("last_sync_error").HasMaxLength(500);
            b.Property(x => x.LastSyncCreated).HasColumnName("last_sync_created");
            b.Property(x => x.LastSyncUpdated).HasColumnName("last_sync_updated");
            b.Property(x => x.LastSyncSkipped).HasColumnName("last_sync_skipped");
            b.Property(x => x.LastFileHash).HasColumnName("last_file_hash");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // ONE source per supplier per org (scope-review decision: multi-source is v2).
            b.HasIndex(x => new { x.OrgId, x.SupplierId }).IsUnique();
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Supplier)
             .WithMany()
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

        // ── order_supplier_suggestions ─────────────────────────────────
        // Ranked supplier candidates for an order that arrived with no supplier, plus the
        // operator's eventual verdict. Sibling of ai_suggestion_decisions, NOT a reuse of it:
        // that table is line-scoped (line_number + suggested_supplier_item_code) and this concept
        // is order-scoped routing. Same tenancy and snake_case conventions.
        modelBuilder.Entity<OrderSupplierSuggestion>(b =>
        {
            b.ToTable("order_supplier_suggestions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.Rank).HasColumnName("rank");
            b.Property(x => x.Score).HasColumnName("score");
            b.Property(x => x.SignalsJson).HasColumnName("signals_json").HasColumnType("jsonb");
            b.Property(x => x.ModelVersion).HasColumnName("model_version");
            // Nullable: NULL means "nobody has decided yet". See the entity doc for why that is a
            // null rather than a fifth vocabulary word.
            b.Property(x => x.Decision).HasColumnName("decision");
            b.Property(x => x.DecidedBy).HasColumnName("decided_by");
            b.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            // Read path: the operator banner asks for one order's live candidates, best first.
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.Rank })
             .HasDatabaseName("IX_order_supplier_suggestions_org_id_order_id_rank");
            // Idempotency key, PARTIAL: at most one UNDECIDED suggestion per (org, order, supplier),
            // so a re-scored order cannot show the same supplier twice. Decided rows are history and
            // accumulate freely — a full unique index would collide the second time a supplier was
            // superseded for the same order, which is a legitimate sequence of events.
            b.HasIndex(x => new { x.OrgId, x.OrderId, x.SupplierId })
             .IsUnique()
             .HasFilter("decision IS NULL")
             .HasDatabaseName("IX_order_supplier_suggestions_live_idempotency");
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
            // Provenance (launch batch 3) — REAL persisted nullable columns (NOT EF-Ignored:
            // the EF-Ignore + ExecuteUpdateAsync silent-drop lesson). Plain uuid, no FK —
            // mirrors purchase_orders.connection_revision_id.
            b.Property(x => x.ConnectionRevisionId).HasColumnName("connection_revision_id");
            b.Property(x => x.ConfigDigest).HasColumnName("config_digest");
            b.Property(x => x.ArtifactSha256).HasColumnName("artifact_sha256");
            // Blob retention: when the artifact blob was purged from R2 (row + hash stay).
            b.Property(x => x.BlobPurgedAt)
             .HasColumnName("blob_purged_at")
             .HasColumnType("timestamptz");
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
            // A3 — deterministic per-artifact delivery idempotency key (nullable; legacy/test-fire null).
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            b.Property(x => x.AttemptedAt)
             .HasColumnName("attempted_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.ResponseCode).HasColumnName("response_code");
            b.Property(x => x.ErrorMessage).HasColumnName("error_message");
            b.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
            // Rejection capture (full NACK body) + ACK round-trip timestamp (Group O reliability).
            b.Property(x => x.ResponseBody).HasColumnName("response_body");
            // WP-19 recovery — the supplier's own Retry-After, bounded (nullable; legacy rows null).
            b.Property(x => x.RetryAfterSeconds).HasColumnName("retry_after_seconds");
            b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            // Provenance (launch batch 3) — REAL persisted nullable columns (see outbound_artifacts note).
            b.Property(x => x.ConnectionRevisionId).HasColumnName("connection_revision_id");
            b.Property(x => x.ConfigDigest).HasColumnName("config_digest");
            b.Property(x => x.ArtifactSha256).HasColumnName("artifact_sha256");
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

        // ── org_plan_history ───────────────────────────────────────────
        // Append-only plan/override history. Overage metering resolves the
        // plan + order-limit override AS OF each billed window's start via
        // the (org_id, effective_from) index — see StripeBillingService.
        modelBuilder.Entity<OrgPlanHistory>(b =>
        {
            b.ToTable("org_plan_history");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Plan).HasColumnName("plan").IsRequired();
            b.Property(x => x.OrderLimitOverride).HasColumnName("order_limit_override");
            b.Property(x => x.EffectiveFrom)
             .HasColumnName("effective_from")
             .HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.EffectiveFrom });
        });

        // ── retention_audit_log ────────────────────────────────────────
        // Append-only evidence trail of the blob-retention sweep: one row per
        // opted-in org per run (mode dry_run = "would delete", delete = "did delete").
        modelBuilder.Entity<RetentionAuditLog>(b =>
        {
            b.ToTable("retention_audit_log");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.RunAt)
             .HasColumnName("run_at")
             .HasColumnType("timestamptz");
            b.Property(x => x.Mode).HasColumnName("mode").IsRequired();
            b.Property(x => x.FilesDeleted).HasColumnName("files_deleted");
            b.Property(x => x.BytesEstimated).HasColumnName("bytes_estimated");
            b.Property(x => x.DetailsJson)
             .HasColumnName("details")
             .HasColumnType("jsonb");
            // Read path: an org's retention history, newest first.
            b.HasIndex(x => new { x.OrgId, x.RunAt })
             .HasDatabaseName("IX_retention_audit_log_org_id_run_at");
            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
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
            // Trusted SSH host-key fingerprint(s) (WP-38). Nullable cleartext text — the digest of a
            // PUBLIC key, so unlike encrypted_password it is meant to be read back and compared.
            b.Property(x => x.HostKeyFingerprints).HasColumnName("host_key_fingerprints");
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
            // Pre-generated order id for resume-on-conflict (Guid.Empty = skip: legacy/terminal).
            b.Property(x => x.OrderId).HasColumnName("order_id").HasDefaultValue(Guid.Empty);
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
            // Pre-generated order id for resume-on-conflict (Guid.Empty = skip: legacy/terminal).
            b.Property(x => x.OrderId).HasColumnName("order_id").HasDefaultValue(Guid.Empty);
            b.Property(x => x.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.BucketName, x.ObjectKey }).IsUnique();
        });

        // ── email_import_records ───────────────────────────────────────
        // Idempotency ledger for IMAP attachment ingestion. The unique index on
        // (OrgId, ImapMessageId, AttachmentHash) is the actual dedupe guarantee: a crash between
        // creating the order stub and flagging the message SEEN re-presents the same unseen message
        // on the next poll, and this index turns the re-import into a no-op rather than a duplicate
        // order. The poller does a pre-insert existence check AND relies on this index to win the
        // race between two concurrent polls of the same mailbox.
        modelBuilder.Entity<EmailImportRecord>(b =>
        {
            b.ToTable("email_import_records");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.ImapMessageId).HasColumnName("imap_message_id").IsRequired();
            b.Property(x => x.AttachmentHash).HasColumnName("attachment_hash").IsRequired();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.FileName).HasColumnName("file_name");
            b.Property(x => x.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamptz");
            b.HasIndex(x => new { x.OrgId, x.ImapMessageId, x.AttachmentHash }).IsUnique()
             .HasDatabaseName("IX_email_import_records_org_id_imap_message_id_attachment_hash");
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

        // ── auto_send_dry_runs (WP-33 stage 1) ─────────────────────────────
        modelBuilder.Entity<AutoSendDryRun>(b =>
        {
            b.ToTable("auto_send_dry_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.SupplierId).HasColumnName("supplier_id");
            b.Property(x => x.WouldHaveSent).HasColumnName("would_have_sent");
            b.Property(x => x.Decision).HasColumnName("decision").IsRequired();
            b.Property(x => x.Channel).HasColumnName("channel");
            b.Property(x => x.OutputFormat).HasColumnName("output_format");
            b.Property(x => x.DecisionDigest).HasColumnName("decision_digest");
            b.Property(x => x.BlockerCount).HasColumnName("blocker_count");
            // Serialized JSON held as a plain string, NOT a JsonDocument. The context's global
            // Ignore<JsonDocument>() does not reach a newly added entity's JsonDocument property —
            // adding one here put JsonDocument itself into the model as an entity type and broke
            // model building for every provider ("No suitable constructor was found for entity type
            // 'JsonDocument'"). A string round-trips through the same jsonb column with none of that.
            b.Property(x => x.Evidence)
             .HasColumnName("evidence")
             .HasColumnType("jsonb");
            b.Property(x => x.EvaluatedAt)
             .HasColumnName("evaluated_at")
             .HasColumnType("timestamptz");

            // THE idempotency boundary. A Hangfire refetch re-runs the evaluation and this index —
            // not the pre-check that usually avoids reaching it — is what refuses the second row.
            b.HasIndex(x => new { x.OrgId, x.OrderId })
             .IsUnique()
             .HasDatabaseName("IX_auto_send_dry_runs_org_id_order_id");

            // The founder's weekly read: "of the orders that opted in, how many would have gone,
            // and what held the rest back?" — one index-backed scan per org.
            b.HasIndex(x => new { x.OrgId, x.WouldHaveSent, x.EvaluatedAt })
             .HasDatabaseName("IX_auto_send_dry_runs_org_id_would_have_sent_evaluated_at");

            b.HasOne(x => x.Organisation)
             .WithMany()
             .HasForeignKey(x => x.OrgId);
            b.HasOne(x => x.Order)
             .WithMany()
             .HasForeignKey(x => x.OrderId);
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
            // Group V4 — optional binding to a reusable RuleDefinition (executor never reads these).
            b.Property(x => x.RuleDefinitionId).HasColumnName("rule_definition_id");
            b.Property(x => x.RuleCode).HasColumnName("rule_code");
            b.HasIndex(x => x.ProfileId).HasDatabaseName("IX_supplier_acceptance_rules_profile_id");
            b.HasIndex(x => x.RuleDefinitionId).HasDatabaseName("IX_supplier_acceptance_rules_rule_definition_id");
        });

        // ── rule_definitions (Group V4 — reusable rule templates / org catalog) ──
        modelBuilder.Entity<RuleDefinition>(b =>
        {
            b.ToTable("rule_definitions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.Code).HasColumnName("code").IsRequired();
            b.Property(x => x.Title).HasColumnName("title").IsRequired();
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.Scope).HasColumnName("scope").IsRequired();
            b.Property(x => x.FieldPath).HasColumnName("field_path").IsRequired();
            b.Property(x => x.Operator).HasColumnName("operator").IsRequired();
            b.Property(x => x.DefaultSeverity).HasColumnName("default_severity").IsRequired();
            b.Property(x => x.DefaultExpectedValue).HasColumnName("default_expected_value");
            b.Property(x => x.ParamHint).HasColumnName("param_hint");
            b.Property(x => x.UblRef).HasColumnName("ubl_ref");
            b.Property(x => x.EdifactRef).HasColumnName("edifact_ref");
            b.Property(x => x.X12Ref).HasColumnName("x12_ref");
            b.Property(x => x.CxmlRef).HasColumnName("cxml_ref");
            b.Property(x => x.IsSystem).HasColumnName("is_system");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.HasOne(x => x.Organisation).WithMany().HasForeignKey(x => x.OrgId);
            // Org-scoped catalog: a code is unique within an org.
            b.HasIndex(x => new { x.OrgId, x.Code })
             .IsUnique()
             .HasDatabaseName("IX_rule_definitions_org_code");
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
            // ── Phase 2 connection-level price-variance guard (additive, defaulted OFF) ──
            b.Property(x => x.PriceVarianceGuardEnabled).HasColumnName("price_variance_guard_enabled").HasDefaultValue(false);
            b.Property(x => x.PriceVarianceThresholdPercent).HasColumnName("price_variance_threshold_percent").HasColumnType("numeric(7,4)").HasDefaultValue(0m);
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

        // ── canonical_field_defs (Phase 2 extensible canonical — Tier-2 user fields) ──
        modelBuilder.Entity<CanonicalFieldDef>(b =>
        {
            b.ToTable("canonical_field_defs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.OrgId).HasColumnName("org_id");
            b.Property(x => x.ConnectionId).HasColumnName("connection_id");
            b.Property(x => x.Key).HasColumnName("key").IsRequired();
            b.Property(x => x.Label).HasColumnName("label").IsRequired();
            b.Property(x => x.Scope).HasColumnName("scope").IsRequired();
            b.Property(x => x.Type).HasColumnName("type").IsRequired();
            b.Property(x => x.StandardsRef).HasColumnName("standards_ref");
            b.Property(x => x.Order).HasColumnName("display_order");
            b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            // Org-scoped lookup; the unique active key per (org, connection, scope) is enforced in app
            // logic (soft-delete means a partial unique index would need a filtered index — kept simple).
            b.HasIndex(x => new { x.OrgId, x.ConnectionId }).HasDatabaseName("IX_canonical_field_defs_org_id_connection_id");
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
            // Launch batch 3 — content-update stamp + test evidence (all nullable; legacy rows null).
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            b.Property(x => x.TestResultJson).HasColumnName("test_result_json").HasColumnType("jsonb");
            b.Property(x => x.TestedAt).HasColumnName("tested_at").HasColumnType("timestamptz");
            b.Property(x => x.TestPassed).HasColumnName("test_passed");
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
            // Phase 1: additive supplier-binding column. Default "" so the migration backfills
            // existing fingerprint rows (NOT NULL with no default would fail on existing data).
            b.Property(x => x.SupplierIdsCsv).HasDefaultValue("");
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

        // ── Organisation query filters ───────────────────────────────────────
        // LAST, deliberately: this walks every entity type configured above and attaches the
        // org filter to each one that carries an organisation column. Running it here means a
        // new entity is covered as soon as it is mapped, with nothing extra to remember.
        // Entities with no org column must be declared in OrgQueryFilters.DeclaredUnscoped;
        // OrgQueryFilterCoverageTests fails the build otherwise.
        //
        // Only for a SCOPED context. OrgScopeModelCacheKeyFactory keeps the scoped and unscoped
        // models in separate cache entries, so an unscoped context (every Worker sweep, migration
        // bootstrap, tenant resolution, and API-key lookup) gets a model with no filters at all
        // and emits exactly the SQL it emitted before this existed.
        if (_scopedOrgId is not null)
            OrgQueryFilters.Apply(modelBuilder, this);
    }

    private static JsonDocument? ParseJsonDoc(string? v) =>
        v == null ? null : JsonDocument.Parse(v);
}
