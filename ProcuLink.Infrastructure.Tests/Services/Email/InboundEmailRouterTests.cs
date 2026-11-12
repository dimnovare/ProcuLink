using ProcuLink.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services.Email;

namespace ProcuLink.Infrastructure.Tests.Services.Email;

/// <summary>
/// Verifies that the inbound-email router resolves the tenant from the recipient address, answers
/// each refusal with the retry kind the mail provider needs, and refuses an organisation-level
/// address. Delivery to a flow's own address is covered by <c>InboundEmailFlowPostgresTests</c>.
/// </summary>
public class InboundEmailRouterTests
{
    private const string Slug = "acme";

    [Fact]
    public async Task UnknownRecipient_ReturnsFailureWithoutCreatingOrders()
    {
        await using var db = CreateDb();
        // No org seeded — the mapping config also points nowhere useful.
        var router = MakeRouter(db, slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "orders@unknown-tenant.proculink.eu",
            Subject:   "Mystery PO",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LocalPartAddressing_RoutesToOrg_WhenInboundDomainConfigured()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        var supplierId = await SeedSupplierAsync(db, orgId);
        var router = MakeRouter(db, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        // Slug is the LOCAL part; host is the single fixed inbound domain.
        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"{Slug}@orders.proculink.eu",
            Subject:   "PO via local-part address",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse("an organisation-level address no longer receives documents");
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent);
        result.OrgId.Should().Be(orgId);
    }

    [Fact]
    public async Task LocalPartAddressing_StripsPlusTag()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);
        var router = MakeRouter(db, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        // "acme+urgent@orders.proculink.eu" must still resolve to org "acme".
        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"{Slug}+urgent@orders.proculink.eu",
            Subject:   "PO with plus-addressing",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse("an organisation-level address no longer receives documents");
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent);
        result.OrgId.Should().Be(orgId);
    }

    [Fact]
    public async Task SubdomainAddressing_StillWorks_WhenInboundDomainConfigured()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);
        // InboundDomain configured, but the recipient uses the legacy subdomain
        // scheme — it must still resolve (back-compat).
        var router = MakeRouter(db, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO via legacy subdomain address",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse("an organisation-level address no longer receives documents");
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent);
        result.OrgId.Should().Be(orgId);
    }

    [Theory]
    [InlineData("+@orders.proculink.eu")]        // empty slug after stripping the +tag
    [InlineData("acme.corp@orders.proculink.eu")] // dots are not valid in a kebab-case slug
    public async Task LocalPartAddressing_InvalidSlug_IsRejected(string toEmail)
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);
        var router = MakeRouter(db, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   toEmail,
            Subject:   "PO with an unroutable local part",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse("the local part does not resolve to a valid tenant slug");
    }

    // ── 11b. GDPR: the audit payload omits raw sender email + subject ────────
    // Finding #8: the audit payload previously stored the raw sender address and
    // subject line (third-party PII) verbatim. The summary must now hash the sender
    // and drop the subject entirely.
    [Fact]
    public void BuildAuditSummary_HashesSender_AndOmitsRawSenderAndSubject()
    {
        const string sender = "buyer@example.com";
        const string subject = "PO #12345 — confidential pricing";
        var payload = new InboundEmailPayload(
            FromEmail: sender,
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   subject,
            Attachments: Array.Empty<InboundAttachment>());

        var json = System.Text.Json.JsonSerializer.Serialize(
            InboundEmailRouter.BuildAuditSummary(payload, extra: null));

        json.Should().NotContain(sender, "the raw sender address is third-party PII and must not be persisted");
        json.Should().NotContain(subject, "the raw subject line is PII and must not be persisted");
        json.Should().Contain(InboundEmailRouter.Sha256Hex(sender),
            "the sender is stored only as a one-way hash for correlation/diagnostics");
    }

    [Fact]
    public async Task UnparseableRecipient_IsPermanent_BecauseNoRetryCanChangeTheAddress()
    {
        await using var db = CreateDb();
        var router = MakeRouter(db, slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "someone@mail.example",
            Subject:   "Not for us",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent,
            "the address is not a ProcuLink inbound address — the tenth delivery reads exactly like the first");
    }

    [Fact]
    public async Task UnknownTenantSlug_IsPermanent_SoStrayMailIsNotDeliveredTenTimes()
    {
        await using var db = CreateDb();
        var router = MakeRouter(db, slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "orders@unknown-tenant.proculink.eu",
            Subject:   "Mystery PO",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent,
            "anything can be mailed to a made-up slug; retrying it ten times only multiplies the noise");
    }

    [Fact]
    public async Task SlugAddressedMail_IsRefused_WhenNoAddressWasIssuedForThatSlug()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        // Give the org a real slug and a real, working inbound address that is NOT the slug. This is
        // the production shape after the legacy grace period ends.
        var org = await db.Organisations.SingleAsync(o => o.Id == orgId);
        org.Slug = "acme-a1b2";
        await db.SaveChangesAsync();
        var router = MakeRouter(db, slug: Slug, orgId: orgId);

        var result = await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "stranger@example.com",
            ToEmail:   "orders@acme-a1b2.proculink.eu",
            Subject:   "Forged PO",
            Attachments: new[] { new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }) }),
            default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull(
            "the slug is public and guessable; resolving on it is what let a stranger address " +
            "another tenant's inbox");
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent);
    }

    [Fact]
    public async Task ConfigTenantMapping_CannotNameAnOrganisation()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        // The old fallback: a config key whose NAME was the caller's own string. It is gone, and it
        // must not come back — an operator-set key is still selected by a value the sender controls.
        var config = InboundAddressTestHarness.Configuration(new Dictionary<string, string?>
        {
            ["Inbound:Postmark:TenantMapping:injected"] = orgId.ToString(),
        });
        var router = new InboundEmailRouter(
            db,
            InboundAddressTestHarness.Create(db, config), PermissiveBilling(), config,
            NullLogger<InboundEmailRouter>.Instance);

        var result = await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "stranger@example.com",
            ToEmail:   "orders@injected.proculink.eu",
            Subject:   "Config-routed PO",
            Attachments: Array.Empty<InboundAttachment>()), default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
    }

    [Fact]
    public async Task UnavailableLookup_IsTransient_SoOurMisconfigurationDefersMailInsteadOfDroppingIt()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        // The pepper is missing, so NOTHING resolves. That is a statement about us, not about this
        // message — and 200 would tell Postmark the mail was handled and end it for good.
        var broken = InboundAddressTestHarness.Configuration(new Dictionary<string, string?>
        {
            ["Security:ApiKeyHashSecret"] = "",
        });

        var router = new InboundEmailRouter(
            db,
            InboundAddressTestHarness.Create(db, broken), PermissiveBilling(), broken,
            NullLogger<InboundEmailRouter>.Instance);

        var result = await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Real PO",
            Attachments: Array.Empty<InboundAttachment>()), default);

        result.Success.Should().BeFalse();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient,
            "a transient rejection is a non-200, which keeps Postmark re-delivering for ~10.5 hours " +
            "and then files the message as re-fireable. Permanent would answer 200 and the purchase " +
            "order would be gone");
    }

    [Fact]
    public async Task RefusalLog_NeverEchoesTheRecipientAddress()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);

        const string secretAddress = "deadbeefdeadbeefdeadbeefdeadbeef";
        var logger = new RecordingLogger();
        var router = MakeRouter(db,
            slug: Slug, orgId: orgId, logger: logger);

        await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "someone@example.com",
            ToEmail:   $"orders@{secretAddress}.proculink.eu",
            Subject:   "Probe",
            Attachments: Array.Empty<InboundAttachment>()), default);

        // An inbound address is a bearer credential. A near-miss echoed into the log is a credential
        // in the log — and these logs are shipped off-box. This repository is public, so a fixture
        // that leaked into an assertion message would be published too.
        logger.Entries.Should().NotBeEmpty("the refusal must still be visible to an operator");
        logger.Entries.Should().OnlyContain(e => !e.Message.Contains(secretAddress),
            "the rejection has to be greppable without printing the credential that was presented");
    }

    [Theory]
    [InlineData("orders@acme.example", "acme.example")]
    [InlineData("Orders@ACME.eXaMpLe", "acme.example")]
    [InlineData("\"Acme Orders\" <orders@mail.acme.example>", "mail.acme.example")]
    [InlineData("a.person+tag@acme.example", "acme.example")]
    public void ExtractSenderDomain_keepsOnlyTheDomain(string from, string expected)
    {
        // The local part is the half that identifies a PERSON. It must not survive this call —
        // the full address keeps its existing SHA-256-only treatment and nothing else.
        Assert.Equal(expected, InboundEmailRouter.ExtractSenderDomain(from));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("someone@localhost")]   // no dot: a host name, never a shared match key
    [InlineData("someone@")]
    public void ExtractSenderDomain_returnsNull_forAnythingThatIsNotClearlyADomain(string? from)
    {
        Assert.Null(InboundEmailRouter.ExtractSenderDomain(from));
    }

    [Fact]
    public async Task OrganisationAddress_IsRefusedPermanent_AndAudited()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        var router = MakeRouter(db, slug: Slug, orgId: orgId);

        var result = await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO to the organisation address",
            Attachments: new[] { new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }) }),
            default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().Be(orgId);
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent,
            "no org address can be minted any more, so a retry can never change the answer");
        db.AuditEvents.Should().ContainSingle(a => a.OrgId == orgId && a.Action == "inbound_email.rejected_orders_retired");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IBillingService"/> that says yes, for the tests whose subject is NOT the
    /// billing gate. A bare <c>Mock&lt;IBillingService&gt;</c> answers <c>false</c>/<c>default</c>,
    /// which would fail every one of them at the gate instead of exercising what they were written
    /// to exercise. Never pass this to a test that is ABOUT a gate — <c>default</c> answers are how
    /// a gate test goes vacuously green.
    /// </summary>
    private static IBillingService PermissiveBilling()
    {
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(
                It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(
                Allowed: true, PilotExpired: false, Plan: PlanConstants.Enterprise, Limit: int.MaxValue));
        return billing.Object;
    }

    private static InboundEmailRouter MakeRouter(
        ProcuLinkDbContext db,
        string slug,
        Guid orgId,
        string? inboundDomain = null,
        ILogger<InboundEmailRouter>? logger = null,
        IBillingService? billing = null)
    {
        var settings = new Dictionary<string, string?>();
        if (inboundDomain is not null)
            settings["Inbound:Postmark:InboundDomain"] = inboundDomain;

        var config = InboundAddressTestHarness.Configuration(settings);

        // Registering the address is what used to be an Inbound:Postmark:TenantMapping config
        // entry. The difference is the whole point of the change: a tenant is now named by a row
        // that had to be ISSUED to it, so a test that wants a non-resolving recipient simply does
        // not seed one — there is no configuration left that could name an organisation instead.
        InboundAddressTestHarness.SeedAddress(db, orgId, slug, config);

        return new InboundEmailRouter(
            db,
            InboundAddressTestHarness.Create(db, config),
            billing ?? PermissiveBilling(),
            config,
            logger ?? NullLogger<InboundEmailRouter>.Instance);
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string accountStatus)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Acme Distribution",
            AccountStatus = accountStatus,
            CreatedAt = DateTime.UtcNow,
            EmailConfigJson = "{}",
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    /// <summary>
    /// Seeds a supplier AND names it as the org's Email-intake default, which is what every
    /// caller here means by "an org whose inbound mail routes". Naming it is now required:
    /// the router picks no supplier on the org's behalf, so merely owning a supplier routes
    /// nothing. Use <see cref="SeedOrgAsync"/> alone for the unrouted-park cases.
    /// </summary>
    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Acme Components",
            CreatedAt = DateTime.UtcNow,
        });

        var org = await db.Organisations.SingleAsync(o => o.Id == orgId);
        org.EmailConfigJson = (EmailPollingConfig.Empty with { DefaultSupplierId = supplierId }).ToJson();

        await db.SaveChangesAsync();
        return supplierId;
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InboundEmailTestDbContext(options);
    }

    /// <summary>
    /// Captures every log line with its level so tests can pin the level of one
    /// message. <see cref="LevelOf"/> fails loudly when the message is absent or
    /// ambiguous — a renamed template must break the test, not silently pass it.
    /// </summary>
    private sealed class RecordingLogger : ILogger<InboundEmailRouter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public LogLevel LevelOf(string messageFragment)
        {
            var matches = Entries
                .Where(e => e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            matches.Should().ContainSingle(
                $"exactly one log line should contain '{messageFragment}'; captured: "
                + string.Join(" | ", Entries.Select(e => $"[{e.Level}] {e.Message}")));

            return matches[0].Level;
        }
    }

    /// <summary>
    /// Minimal in-memory DbContext that materialises only what the router
    /// touches: Organisations, Suppliers, and AuditEvents. Other entities are
    /// ignored to avoid fabricating fixtures.
    /// </summary>
    private sealed class InboundEmailTestDbContext : ProcuLinkDbContext
    {
        public InboundEmailTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<WorkerHealthAlertCooldown>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
            });

            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.User);
                // JsonDocument is not supported by InMemory; ignore the payload
                // column so audit writes don't blow up. The router writes audit
                // best-effort and swallows exceptions either way.
                b.Ignore(x => x.Payload);
            });
        }
    }
}
