using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for /api/billing/status and /api/billing/webhook.
/// Uses Moq for IBillingService + IAiUsageTracker + ICurrentTenantService.
/// The Stripe webhook tests exercise only the 400 path (bad/missing signature)
/// because constructing a valid Stripe signature requires the raw secret.
/// </summary>
public class BillingControllerTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static BillingTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new BillingTestDbContext(opts);
    }

    private static IConfiguration MakeConfig(string? webhookSecret = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:WebhookSecret"]   = webhookSecret ?? "whsec_test_placeholder",
                ["Frontend:Url"]           = "http://localhost:8082",
                ["Stripe:GrowthPriceId"]   = "price_growth_test",
                ["Stripe:GrowthYearlyPriceId"] = "price_growth_yearly_test",
                ["Stripe:OperationsPriceId"] = "price_ops_test",
                ["Stripe:OperationsYearlyPriceId"] = "price_ops_yearly_test",
                ["Stripe:IntegrationPriceId"] = "price_int_test",
                ["Stripe:IntegrationYearlyPriceId"] = "price_int_yearly_test",
                ["Stripe:DistributorPriceId"] = "price_dist_test",
                ["Stripe:DistributorYearlyPriceId"] = "price_dist_yearly_test",
            })
            .Build();

    private static (
        BillingController Controller,
        Mock<IBillingService>   BillingSvc,
        Mock<IAiUsageTracker>   AiUsage,
        Guid                    OrgId,
        ProcuLinkDbContext      Db)
    Build(ProcuLinkDbContext? db = null, string? webhookSecret = null)
    {
        db ??= MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        var aiUsage = new Mock<IAiUsageTracker>();

        var ctrl = new BillingController(
            billing.Object,
            tenant.Object,
            MakeConfig(webhookSecret),
            NullLogger<BillingController>.Instance,
            db,
            aiUsage.Object);

        return (ctrl, billing, aiUsage, orgId, db);
    }

    private static void SetHttpContext(
        BillingController ctrl,
        string            body        = "{}",
        string?           stripeHeader = null)
    {
        var httpContext = new DefaultHttpContext();

        if (stripeHeader is not null)
            httpContext.Request.Headers["Stripe-Signature"] = stripeHeader;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body          = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── GET /api/billing/status ───────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_Returns200WithBillingSnapshot()
    {
        var (ctrl, billing, _, orgId, _) = Build();

        var snapshot = new BillingStatus(
            Plan:                   PlanConstants.Growth,
            AccountStatus:          AccountStatusConstants.Active,
            OrdersThisMonth:        12,
            OrderLimit:             150,
            SuppliersUsed:          2,
            SupplierLimit:          5,
            TrialStartedAt:         null,
            TrialEndsAt:            null,
            IsTrialExpired:         false,
            IsOrderLimitReached:    false,
            IsSupplierLimitReached: false,
            CanProcessOrders:       true,
            CanAddSupplier:         true,
            StripeCustomerId:       "cus_test",
            StripeSubscriptionId:   "sub_test");

        billing
            .Setup(b => b.GetStatusAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var result = await ctrl.GetStatus(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task GetStatus_PilotTenant_ReturnsPilotPlanSnapshot()
    {
        var (ctrl, billing, _, orgId, _) = Build();

        var snapshot = new BillingStatus(
            Plan:                   PlanConstants.Pilot,
            AccountStatus:          AccountStatusConstants.Trialing,
            OrdersThisMonth:        3,
            OrderLimit:             20,
            SuppliersUsed:          1,
            SupplierLimit:          1,
            TrialStartedAt:         DateTime.UtcNow.AddDays(-5),
            TrialEndsAt:            DateTime.UtcNow.AddDays(9),
            IsTrialExpired:         false,
            IsOrderLimitReached:    false,
            IsSupplierLimitReached: false,
            CanProcessOrders:       true,
            CanAddSupplier:         false,
            StripeCustomerId:       null,
            StripeSubscriptionId:   null);

        billing
            .Setup(b => b.GetStatusAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var result = await ctrl.GetStatus(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeAssignableTo<BillingStatus>().Subject;
        returned.Plan.Should().Be(PlanConstants.Pilot);
        returned.CanProcessOrders.Should().BeTrue();
        returned.CanAddSupplier.Should().BeFalse();
    }

    // ── POST /api/billing/webhook ─────────────────────────────────────────────

    [Fact]
    public async Task Webhook_AbsentStripeSignatureHeader_Returns400()
    {
        var (ctrl, _, _, _, _) = Build();

        // No Stripe-Signature header at all — FirstOrDefault() returns null.
        // Before the null-guard fix, ConstructEvent threw NullReferenceException
        // (not StripeException), which the catch block missed, returning 500.
        // The guard must intercept this and return a clean 400.
        SetHttpContext(ctrl, body: "{}", stripeHeader: null);

        var result = await ctrl.Webhook(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an absent Stripe-Signature header must return 400, not 500");
    }

    [Fact]
    public async Task Webhook_MissingStripeSignatureHeader_Returns400()
    {
        var (ctrl, _, _, _, _) = Build();

        // An empty Stripe-Signature string is also rejected by the null-or-empty guard.
        SetHttpContext(ctrl, body: "{}", stripeHeader: string.Empty);

        var result = await ctrl.Webhook(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an empty Stripe-Signature must return 400");
    }

    [Fact]
    public async Task Webhook_InvalidStripeSignature_Returns400()
    {
        var (ctrl, _, _, _, _) = Build(webhookSecret: "whsec_realSecretWouldBeHere");

        // A syntactically plausible but cryptographically invalid signature header
        SetHttpContext(ctrl, body: "{\"type\":\"checkout.session.completed\"}",
                       stripeHeader: "t=1234567890,v1=badhex00000000000000000000000000000000000000000000000000000000");

        var result = await ctrl.Webhook(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an invalid Stripe signature must return 400");
    }

    // ── POST /api/billing/checkout ────────────────────────────────────────────

    [Fact]
    public async Task CreateCheckout_InvalidPlan_Returns400()
    {
        var (ctrl, _, _, _, _) = Build();

        var result = await ctrl.CreateCheckout(
            new CheckoutRequest("free_forever"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateCheckout_YearlyInterval_PassesYearlyToBillingService()
    {
        var (ctrl, billing, _, orgId, _) = Build();

        billing
            .Setup(b => b.CreateCheckoutSessionAsync(
                orgId,
                PlanConstants.Growth,
                "http://localhost:8082",
                "yearly",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://checkout.stripe.com/yearly");

        var result = await ctrl.CreateCheckout(
            new CheckoutRequest(PlanConstants.Growth, "yearly"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(new { url = "https://checkout.stripe.com/yearly" });
    }

    // ── Webhook handler unit tests ────────────────────────────────────────────

    [Fact]
    public async Task HandleCheckoutCompleted_UpgradesOrgPlanAndSetsActiveStatus()
    {
        var (ctrl, _, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id             = orgId,
            Plan           = PlanConstants.Pilot,
            AccountStatus  = AccountStatusConstants.Trialing,
            ClerkOrgId     = "org_test",
            Name           = "Test Org",
            Slug           = "test-org",
            TrialStartedAt = DateTime.UtcNow.AddDays(-3),
        });
        await db.SaveChangesAsync();

        // SubscriptionId = null skips GetSubscriptionStatusAsync / GetSubscriptionPriceIdAsync
        // (those hit the real Stripe HTTP API). mappedPlan falls back to the metadata "plan" value.
        var session = new Stripe.Checkout.Session
        {
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = orgId.ToString(),
                ["plan"]   = PlanConstants.Growth,
            },
            Id             = "cs_test_checkout",
            SubscriptionId = null,
            CustomerId     = "cus_checkout_test",
        };

        await ctrl.HandleCheckoutCompletedAsync(session, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.Plan.Should().Be(PlanConstants.Growth);
        updated.AccountStatus.Should().Be(AccountStatusConstants.Active);
        updated.StripeCustomerId.Should().Be("cus_checkout_test");
    }

    [Fact]
    public async Task HandleSubscriptionDeleted_RevertsOrgToPilotReadOnly()
    {
        var (ctrl, _, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id                   = orgId,
            Plan                 = PlanConstants.Growth,
            AccountStatus        = AccountStatusConstants.Active,
            StripeCustomerId     = "cus_del_test",
            StripeSubscriptionId = "sub_del_test",
            ClerkOrgId           = "org_del",
            Name                 = "Del Org",
            Slug                 = "del-org",
        });
        await db.SaveChangesAsync();

        var sub = new Stripe.Subscription
        {
            Id         = "sub_del_test",
            CustomerId = "cus_del_test",
        };

        await ctrl.HandleSubscriptionDeletedAsync(sub, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.Plan.Should().Be(PlanConstants.Pilot);
        updated.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly);
        updated.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task HandleSubscriptionUpdated_WhenPastDue_SetsAccountStatusPastDue()
    {
        var (ctrl, _, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id                   = orgId,
            Plan                 = PlanConstants.Growth,
            AccountStatus        = AccountStatusConstants.Active,
            StripeCustomerId     = "cus_upd_test",
            StripeSubscriptionId = "sub_upd_test",
            ClerkOrgId           = "org_upd",
            Name                 = "Upd Org",
            Slug                 = "upd-org",
        });
        await db.SaveChangesAsync();

        // Empty Items.Data → priceId = null → plan unchanged; Status drives AccountStatus.
        var sub = new Stripe.Subscription
        {
            Id         = "sub_upd_test",
            CustomerId = "cus_upd_test",
            Status     = "past_due",
            Items      = new Stripe.StripeList<Stripe.SubscriptionItem>
            {
                Data = new List<Stripe.SubscriptionItem>(),
            },
        };

        await ctrl.HandleSubscriptionUpdatedAsync(sub, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.AccountStatus.Should().Be(AccountStatusConstants.PastDue);
        updated.Plan.Should().Be(PlanConstants.Growth, "plan must be unchanged when no price mapping is found");
    }

    // ── Minimal in-memory DbContext ──────────────────────────────────────────

    private sealed class BillingTestDbContext : ProcuLinkDbContext
    {
        public BillingTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SchemaFingerprint>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.Suppliers);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });

            // PurchaseOrderEntity is needed by HandleSubscriptionDeletedAsync's AnyAsync query.
            // CanonicalJson (JsonDocument) and all navigations are ignored for the in-memory provider.
            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.CanonicalJson);
            });
        }
    }
}
