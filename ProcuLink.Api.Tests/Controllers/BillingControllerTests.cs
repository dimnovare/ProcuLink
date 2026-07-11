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
            StripeSubscriptionId:   "sub_test",
            OverageOrders:          0,
            OverageAmountEur:       0m,
            NearLimit:              false,
            AtLimit:                false);

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
            StripeSubscriptionId:   null,
            OverageOrders:          0,
            OverageAmountEur:       0m,
            NearLimit:              false,
            AtLimit:                false);

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

    [Fact]
    public async Task Webhook_ApiVersionMismatch_WithValidSignature_DoesNotReturn400()
    {
        // Stripe.net 51.1.0 pins the SDK to API version 2026-04-22.dahlia. If the
        // Stripe dashboard endpoint (or the account default) is ever on a DIFFERENT
        // version, every delivered event carries that other api_version. With the
        // EventUtility.ConstructEvent default (throwOnApiVersionMismatch:true) a
        // mismatch throws StripeException → the controller returns 400 → Stripe
        // retries forever → plans never unlock (silent billing failure). The fix
        // passes throwOnApiVersionMismatch:false so version drift no longer drops
        // events. Signature verification is UNAFFECTED (still fully enforced — see
        // Webhook_InvalidStripeSignature_Returns400 above).
        const string secret = "whsec_version_mismatch_test";
        var (ctrl, _, _, _, _) = Build(webhookSecret: secret);

        // An event stamped with an OLD api_version and an UNHANDLED type (the
        // dispatcher no-ops on it) so this test isolates the version-gate behavior.
        const string body =
            "{\"id\":\"evt_versiontest\",\"object\":\"event\",\"api_version\":\"2020-08-27\"," +
            "\"type\":\"customer.created\",\"data\":{\"object\":{\"id\":\"cus_x\",\"object\":\"customer\"}}}";

        // Build a cryptographically VALID Stripe signature for this exact body.
        // Stripe's scheme: signed payload = "{timestamp}.{body}", HMAC-SHA256 with
        // the webhook secret as the key, hex-encoded → the v1 component.
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{body}"));
        var header = $"t={ts},v1={Convert.ToHexString(hash).ToLowerInvariant()}";

        SetHttpContext(ctrl, body: body, stripeHeader: header);

        var result = await ctrl.Webhook(CancellationToken.None);

        // With throwOnApiVersionMismatch:false the event parses despite the version
        // mismatch; the unhandled type falls through the dispatcher → 200 OK.
        result.Should().BeOfType<OkResult>(
            "a valid-signature event with a mismatched api_version must NOT 400 — " +
            "ConstructEvent must use throwOnApiVersionMismatch:false");
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
    public async Task HandleCheckoutCompleted_WithOperationsPlanMetadata_UpgradesToOperationsPlan()
    {
        // Verifies that a checkout session with "plan"="operations" in the metadata
        // (and SubscriptionId = null so no live Stripe HTTP call is made) upgrades
        // the org from Pilot to Operations, and sets AccountStatus to Active.
        var db = MakeDb();
        var (ctrl, _, _, orgId, _) = Build(db);

        db.Organisations.Add(new Organisation
        {
            Id             = orgId,
            Plan           = PlanConstants.Pilot,
            AccountStatus  = AccountStatusConstants.Trialing,
            ClerkOrgId     = "org_ops_test",
            Name           = "Ops Test Org",
            Slug           = "ops-test-org",
            TrialStartedAt = DateTime.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();

        var session = new Stripe.Checkout.Session
        {
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = orgId.ToString(),
                ["plan"]   = PlanConstants.Operations,
            },
            Id             = "cs_test_ops",
            SubscriptionId = null,   // keeps test offline — no Stripe HTTP call
            CustomerId     = "cus_ops_test",
        };

        await ctrl.HandleCheckoutCompletedAsync(session, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.Plan.Should().Be(PlanConstants.Operations,
            "checkout completed with Operations plan metadata must upgrade the org to Operations");
        updated.AccountStatus.Should().Be(AccountStatusConstants.Active,
            "a completed checkout (non-trialing sub) must set AccountStatus to Active");
        updated.StripeCustomerId.Should().Be("cus_ops_test");
    }

    [Fact]
    public async Task HandleSubscriptionDeleted_WhenOrgAlreadyOnPilotWithNoSubscription_DoesNotMutateOrg()
    {
        // Guard: if the org is already on Pilot with StripeSubscriptionId == null,
        // HandleSubscriptionDeletedAsync returns early without mutating anything.
        // This prevents a cancelled customer from accidentally getting a fresh
        // read-only reset (which could, in a bug, clear TrialStartedAt and give
        // them a new window if billing re-read the field).
        var db = MakeDb();
        var (ctrl, _, _, orgId, _) = Build(db);

        var originalTrialStart = DateTime.UtcNow.AddDays(-30);
        var originalTrialEnd   = originalTrialStart.AddDays(14);

        db.Organisations.Add(new Organisation
        {
            Id                   = orgId,
            Plan                 = PlanConstants.Pilot,
            AccountStatus        = AccountStatusConstants.ReadOnly,
            StripeCustomerId     = "cus_expired_pilot",
            StripeSubscriptionId = null,     // already reverted — no active sub
            ClerkOrgId           = "org_expired",
            Name                 = "Expired Pilot Org",
            Slug                 = "expired-pilot-org",
            TrialStartedAt       = originalTrialStart,
            TrialEndsAt          = originalTrialEnd,
        });
        await db.SaveChangesAsync();

        var sub = new Stripe.Subscription
        {
            Id         = "sub_already_cancelled",
            CustomerId = "cus_expired_pilot",
        };

        await ctrl.HandleSubscriptionDeletedAsync(sub, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);

        // The early-return guard in HandleSubscriptionDeletedAsync fires because
        // Plan == Pilot AND StripeSubscriptionId is null.  Nothing must change.
        updated!.Plan.Should().Be(PlanConstants.Pilot,
            "plan must remain Pilot — handler exits early when already on Pilot with no subscription");
        updated.AccountStatus.Should().Be(AccountStatusConstants.ReadOnly,
            "AccountStatus must not be reset — already read-only, handler must not touch it");
        updated.TrialStartedAt.Should().BeCloseTo(originalTrialStart, TimeSpan.FromSeconds(1),
            "TrialStartedAt must not change — no fresh trial is granted");
        updated.StripeSubscriptionId.Should().BeNull(
            "StripeSubscriptionId must remain null — no mutation occurred");
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

        await ctrl.HandleSubscriptionUpdatedAsync(sub, DateTime.UtcNow, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.AccountStatus.Should().Be(AccountStatusConstants.PastDue);
        updated.Plan.Should().Be(PlanConstants.Growth, "plan must be unchanged when no price mapping is found");
    }

    // ── Webhook event-ordering guard (Finding 3) ─────────────────────────────
    // Stripe does not guarantee webhook delivery order. A STALE customer.subscription.
    // updated (past_due) delivered AFTER a newer (active) event must not overwrite the
    // fresh Active state with PastDue (read-only) — that would freeze a paying, now-current
    // customer until the daily reconcile heals it ~24h later. The handler stamps the org
    // with each applied event's Stripe `created` timestamp and ignores any updated event
    // whose `created` predates the last-applied one.

    private static Stripe.Subscription UpdatedSub(string status) => new()
    {
        Id         = "sub_order_guard",
        CustomerId = "cus_order_guard",
        Status     = status,
        Items      = new Stripe.StripeList<Stripe.SubscriptionItem>
        {
            Data = new List<Stripe.SubscriptionItem>(), // no price → plan unchanged; status drives account_status
        },
    };

    private static Organisation OrderGuardOrg(Guid orgId) => new()
    {
        Id                   = orgId,
        Plan                 = PlanConstants.Growth,
        AccountStatus        = AccountStatusConstants.Active,
        StripeCustomerId     = "cus_order_guard",
        StripeSubscriptionId = "sub_order_guard",
        ClerkOrgId           = "org_order_guard",
        Name                 = "Order Guard Org",
        Slug                 = "order-guard-org",
    };

    [Fact]
    public async Task HandleSubscriptionUpdated_StalePastDueDeliveredAfterActive_DoesNotFreezeActivePayer()
    {
        var (ctrl, _, _, orgId, db) = Build();
        db.Organisations.Add(OrderGuardOrg(orgId));
        await db.SaveChangesAsync();

        var activeCreated  = new DateTime(2026, 7, 11, 10, 05, 0, DateTimeKind.Utc); // newer
        var pastDueCreated = new DateTime(2026, 7, 11, 10, 00, 0, DateTimeKind.Utc); // older — generated first, delivered second

        // Delivery is out of order: the NEWER 'active' event arrives first…
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("active"), activeCreated, CancellationToken.None);
        // …then the STALE 'past_due' event (older created) arrives second.
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("past_due"), pastDueCreated, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.AccountStatus.Should().Be(AccountStatusConstants.Active,
            "a stale past_due event delivered after a newer active event must NOT overwrite Active with read-only PastDue");
        updated.StripeSubscriptionStatus.Should().Be("active",
            "the authoritative stored Stripe status must remain the newer 'active'");
    }

    [Fact]
    public async Task HandleSubscriptionUpdated_InOrderPastDueAfterActive_StillApplies()
    {
        var (ctrl, _, _, orgId, db) = Build();
        db.Organisations.Add(OrderGuardOrg(orgId));
        await db.SaveChangesAsync();

        var activeCreated  = new DateTime(2026, 7, 11, 10, 00, 0, DateTimeKind.Utc); // older
        var pastDueCreated = new DateTime(2026, 7, 11, 10, 05, 0, DateTimeKind.Utc); // newer

        // In-order delivery: active first, then a genuinely newer past_due.
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("active"), activeCreated, CancellationToken.None);
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("past_due"), pastDueCreated, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.AccountStatus.Should().Be(AccountStatusConstants.PastDue,
            "a genuinely newer past_due event must still apply — the guard blocks only out-of-order (older) events");
        updated.StripeSubscriptionStatus.Should().Be("past_due");
    }

    [Fact]
    public async Task HandleSubscriptionUpdated_RedeliveredSameEvent_IsIdempotent()
    {
        var (ctrl, _, _, orgId, db) = Build();
        db.Organisations.Add(OrderGuardOrg(orgId));
        await db.SaveChangesAsync();

        var created = new DateTime(2026, 7, 11, 10, 00, 0, DateTimeKind.Utc);

        // The SAME event redelivered (equal created) must re-apply idempotently, not be
        // dropped — the guard blocks only STRICTLY older events.
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("past_due"), created, CancellationToken.None);
        await ctrl.HandleSubscriptionUpdatedAsync(UpdatedSub("past_due"), created, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.AccountStatus.Should().Be(AccountStatusConstants.PastDue,
            "a redelivered event with an equal created timestamp must still apply (idempotent re-apply)");
    }

    // ── Webhook calls billing events THROUGH the IBillingService interface ────
    // Regression guard for the removed runtime downcast (IBillingService ->
    // StripeBillingService). The controller now resolves a Moq IBillingService
    // (which is NOT a StripeBillingService); the old `_billing as StripeBillingService`
    // cast would have been null and silently skipped these calls. Verifying the
    // mock was invoked proves the calls flow through the interface.

    [Fact]
    public async Task HandleCheckoutCompleted_EmitsBillingUpgraded_ThroughInterface()
    {
        var (ctrl, billing, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id             = orgId,
            Plan           = PlanConstants.Pilot,
            AccountStatus  = AccountStatusConstants.Trialing,
            ClerkOrgId     = "org_emit_up",
            Name           = "Emit Up Org",
            Slug           = "emit-up-org",
            TrialStartedAt = DateTime.UtcNow.AddDays(-3),
        });
        await db.SaveChangesAsync();

        var session = new Stripe.Checkout.Session
        {
            Metadata = new Dictionary<string, string>
            {
                ["org_id"] = orgId.ToString(),
                ["plan"]   = PlanConstants.Growth,
            },
            Id             = "cs_emit_up",
            SubscriptionId = null,          // keep offline — no live Stripe HTTP call
            CustomerId     = "cus_emit_up",
        };

        await ctrl.HandleCheckoutCompletedAsync(session, CancellationToken.None);

        billing.Verify(b => b.EmitBillingUpgradedAsync(
            orgId,
            PlanConstants.Pilot,
            PlanConstants.Growth,
            "cs_emit_up",
            It.IsAny<CancellationToken>()), Times.Once,
            "checkout.session.completed must emit billing_upgraded THROUGH IBillingService, not a downcast");
    }

    [Fact]
    public async Task HandleSubscriptionDeleted_EmitsBillingCancelled_ThroughInterface()
    {
        var (ctrl, billing, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id                   = orgId,
            Plan                 = PlanConstants.Growth,
            AccountStatus        = AccountStatusConstants.Active,
            StripeCustomerId     = "cus_emit_cancel",
            StripeSubscriptionId = "sub_emit_cancel",
            ClerkOrgId           = "org_emit_cancel",
            Name                 = "Emit Cancel Org",
            Slug                 = "emit-cancel-org",
        });
        await db.SaveChangesAsync();

        var sub = new Stripe.Subscription
        {
            Id         = "sub_emit_cancel",
            CustomerId = "cus_emit_cancel",
        };

        await ctrl.HandleSubscriptionDeletedAsync(sub, CancellationToken.None);

        billing.Verify(b => b.EmitBillingCancelledAsync(
            orgId,
            PlanConstants.Growth,
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once,
            "customer.subscription.deleted must emit billing_cancelled THROUGH IBillingService, not a downcast");
    }

    [Fact]
    public async Task HandleInvoiceCreated_ComputesAndBillsOverage_ThroughInterface()
    {
        var (ctrl, billing, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id               = orgId,
            Plan             = PlanConstants.Operations,
            AccountStatus    = AccountStatusConstants.Active,
            StripeCustomerId = "cus_overage",
            ClerkOrgId       = "org_overage",
            Name             = "Overage Org",
            Slug             = "overage-org",
        });
        await db.SaveChangesAsync();

        // Overage path: compute returns > 0 (clears the `<= 0` guard), then bill is
        // invoked through the interface. The mock must return a real result so the
        // controller can read result.OverageOrders for its log line.
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                orgId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        billing
            .Setup(b => b.BillOverageForInvoiceAsync(
                orgId, It.IsAny<string>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid o, string key, int orders, CancellationToken _) =>
                new OverageBillingResult(o, key, orders, orders * 50L, AlreadyBilled: false, StripeItemId: "ii_test"));

        var periodStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd   = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var invoice = new Stripe.Invoice
        {
            Id          = "in_overage",
            CustomerId  = "cus_overage",
            PeriodStart = periodStart,
            PeriodEnd   = periodEnd,
            Parent      = new Stripe.InvoiceParent
            {
                SubscriptionDetails = new Stripe.InvoiceParentSubscriptionDetails
                {
                    SubscriptionId = "sub_overage",
                },
            },
        };

        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        var expectedKey = BillingController.BuildPeriodBillingKey(orgId, periodStart);
        billing.Verify(b => b.ComputePeriodOverageOrdersAsync(
            orgId, periodStart, periodEnd, It.IsAny<CancellationToken>()), Times.Once,
            "invoice.created must compute overage THROUGH IBillingService");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            orgId, expectedKey, 7, It.IsAny<CancellationToken>()), Times.Once,
            "invoice.created must bill overage THROUGH IBillingService, not a downcast");
    }

    [Fact]
    public async Task HandleInvoiceCreated_WhenNoOverage_DoesNotBill_ThroughInterface()
    {
        var (ctrl, billing, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id               = orgId,
            Plan             = PlanConstants.Operations,
            AccountStatus    = AccountStatusConstants.Active,
            StripeCustomerId = "cus_no_overage",
            ClerkOrgId       = "org_no_overage",
            Name             = "No Overage Org",
            Slug             = "no-overage-org",
        });
        await db.SaveChangesAsync();

        // Compute returns 0 → the `<= 0` guard must short-circuit before billing.
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                orgId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var invoice = new Stripe.Invoice
        {
            Id         = "in_no_overage",
            CustomerId = "cus_no_overage",
            Parent     = new Stripe.InvoiceParent
            {
                SubscriptionDetails = new Stripe.InvoiceParentSubscriptionDetails
                {
                    SubscriptionId = "sub_no_overage",
                },
            },
        };

        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        billing.Verify(b => b.ComputePeriodOverageOrdersAsync(
            orgId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "no overage must mean no billing call");
    }

    // ── Yearly billing: price-id → plan webhook mapping ───────────────────────
    // MapPriceIdToPlan must resolve YEARLY price ids to the same plan as their
    // monthly siblings — otherwise a customer.subscription.updated event for an
    // annual subscription would silently leave the org's plan unmapped.

    [Theory]
    [InlineData("price_growth_yearly_test", PlanConstants.Growth)]
    [InlineData("price_ops_yearly_test", PlanConstants.Operations)]
    [InlineData("price_int_yearly_test", PlanConstants.Integration)]
    [InlineData("price_dist_yearly_test", PlanConstants.Distributor)]
    [InlineData("price_growth_test", PlanConstants.Growth)]
    [InlineData("price_ops_test", PlanConstants.Operations)]
    [InlineData("price_int_test", PlanConstants.Integration)]
    [InlineData("price_dist_test", PlanConstants.Distributor)]
    public async Task HandleSubscriptionUpdated_PriceId_MapsToCorrectPlan_IncludingYearly(
        string priceId, string expectedPlan)
    {
        var (ctrl, _, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id                   = orgId,
            Plan                 = PlanConstants.Pilot,
            AccountStatus        = AccountStatusConstants.Trialing,
            StripeCustomerId     = "cus_interval_map",
            ClerkOrgId           = "org_interval_map",
            Name                 = "Interval Map Org",
            Slug                 = "interval-map-org",
        });
        await db.SaveChangesAsync();

        var sub = new Stripe.Subscription
        {
            Id         = "sub_interval_map",
            CustomerId = "cus_interval_map",
            Status     = "active",
            Items      = new Stripe.StripeList<Stripe.SubscriptionItem>
            {
                Data = new List<Stripe.SubscriptionItem>
                {
                    new() { Price = new Stripe.Price { Id = priceId } },
                },
            },
        };

        await ctrl.HandleSubscriptionUpdatedAsync(sub, DateTime.UtcNow, CancellationToken.None);

        var updated = await db.Organisations.FindAsync(orgId);
        updated!.Plan.Should().Be(expectedPlan,
            $"price id '{priceId}' must map to plan '{expectedPlan}' (yearly ids included)");
        updated.StripePriceId.Should().Be(priceId, "the raw price id must be persisted for interval derivation");
        updated.AccountStatus.Should().Be(AccountStatusConstants.Active);
    }

    // ── Yearly billing: invoice-period → calendar-month overage windows ───────
    // The order quota is MONTHLY on every paid plan regardless of payment interval.
    // A yearly renewal invoice spans ~12 months; metering it as ONE window would
    // count the whole year's orders against the monthly cap. SplitBillingPeriod…
    // must decompose multi-month periods into calendar-month windows while leaving
    // single-month (monthly subscription) periods byte-identical.

    [Fact]
    public void SplitBillingPeriod_MonthlyAnchorPeriod_PassesThroughUnchanged()
    {
        // A mid-month Stripe anchor period (Jan 15 → Feb 15, 31 days) must NOT be
        // decomposed — the monthly billing path stays byte-identical.
        var start = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var windows = BillingController.SplitBillingPeriodIntoMonthlyWindows(start, end);

        windows.Should().ContainSingle();
        windows[0].Should().Be((start, end));
    }

    [Fact]
    public void SplitBillingPeriod_CalendarMonthPeriod_PassesThroughUnchanged()
    {
        var start = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var windows = BillingController.SplitBillingPeriodIntoMonthlyWindows(start, end);

        windows.Should().ContainSingle();
        windows[0].Should().Be((start, end));
    }

    [Fact]
    public void SplitBillingPeriod_YearlyPeriod_SplitsIntoContiguousCalendarMonths()
    {
        // Mid-month annual anchor: Jun 15 2026 → Jun 15 2027.
        var start = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2027, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var windows = BillingController.SplitBillingPeriodIntoMonthlyWindows(start, end);

        // Partial first month + 11 full months + partial last month = 13 windows.
        windows.Should().HaveCount(13);
        windows[0].Should().Be((start, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            "the first window runs from the anchor day to the next calendar-month boundary");
        windows[^1].Should().Be((new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc), end),
            "the last window runs from the final month boundary to the anchor day");

        // Contiguity: no gaps, no overlaps, every window ≤ 1 calendar month.
        for (var i = 1; i < windows.Count; i++)
            windows[i].Start.Should().Be(windows[i - 1].End, "windows must be contiguous");
        foreach (var (s, e) in windows)
        {
            e.Should().BeAfter(s);
            (e - s).TotalDays.Should().BeLessThanOrEqualTo(31);
        }
    }

    [Fact]
    public void SplitBillingPeriod_YearlyPeriodOnCalendarBoundary_IsTwelveFullMonths()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var windows = BillingController.SplitBillingPeriodIntoMonthlyWindows(start, end);

        windows.Should().HaveCount(12);
        windows[0].Should().Be((start, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        windows[^1].Should().Be((new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), end));
    }

    [Fact]
    public async Task HandleInvoiceCreated_YearlyInvoicePeriod_MetersEachCalendarMonth_NeverTheWholeYear()
    {
        var (ctrl, billing, _, orgId, db) = Build();

        db.Organisations.Add(new Organisation
        {
            Id               = orgId,
            Plan             = PlanConstants.Operations,
            AccountStatus    = AccountStatusConstants.Active,
            StripeCustomerId = "cus_yearly_overage",
            ClerkOrgId       = "org_yearly_overage",
            Name             = "Yearly Overage Org",
            Slug             = "yearly-overage-org",
        });
        await db.SaveChangesAsync();

        // A yearly subscription's renewal invoice covers the whole closed year.
        var periodStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd   = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Only March went over the cap: compute returns 4 for the March window, 0 otherwise.
        var marchStart = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var aprilStart = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                orgId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTime s, DateTime _, CancellationToken _) =>
                s == marchStart ? 4 : 0);
        billing
            .Setup(b => b.BillOverageForInvoiceAsync(
                orgId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid o, string key, int orders, CancellationToken _) =>
                new OverageBillingResult(o, key, orders, orders * 50L, AlreadyBilled: false, StripeItemId: "ii_march"));

        var invoice = new Stripe.Invoice
        {
            Id          = "in_yearly",
            CustomerId  = "cus_yearly_overage",
            PeriodStart = periodStart,
            PeriodEnd   = periodEnd,
            Parent      = new Stripe.InvoiceParent
            {
                SubscriptionDetails = new Stripe.InvoiceParentSubscriptionDetails
                {
                    SubscriptionId = "sub_yearly",
                },
            },
        };

        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        // The year is metered as 12 calendar-month windows — NEVER the raw 12-month span.
        billing.Verify(b => b.ComputePeriodOverageOrdersAsync(
            orgId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(12), "a yearly invoice period must be metered per calendar month");
        billing.Verify(b => b.ComputePeriodOverageOrdersAsync(
            orgId, periodStart, periodEnd, It.IsAny<CancellationToken>()),
            Times.Never, "the whole-year window must never be counted against the monthly cap");
        billing.Verify(b => b.ComputePeriodOverageOrdersAsync(
            orgId, marchStart, aprilStart, It.IsAny<CancellationToken>()),
            Times.Once, "March must be metered as exactly [Mar 1, Apr 1)");

        // Only the over-cap month is billed, keyed on ITS window start (own ledger row).
        var marchKey = BillingController.BuildPeriodBillingKey(orgId, marchStart);
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            orgId, marchKey, 4, It.IsAny<CancellationToken>()), Times.Once,
            "the over-cap month is billed with its own per-month period key");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once, "under-cap months must not be billed at all");
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
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
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
