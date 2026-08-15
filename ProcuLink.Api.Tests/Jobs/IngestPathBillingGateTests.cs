using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// WP-11 defect #3 — <b>every ingest path must pass the same billing gate</b>.
///
/// <para>Three ways into the system skipped the check the IMAP poller has applied since
/// it shipped (<c>EmailPollOrgJob.cs</c>: <c>HasFeatureAsync(EmailIngestion)</c> → early
/// return):</para>
/// <list type="bullet">
///   <item><b>REST ingress</b> (<see cref="IngressController"/>) had no billing gate at
///   all, so a frozen / trial-expired org could keep pushing orders through the API long
///   after the UI stopped accepting uploads.</item>
///   <item><b>SFTP polling</b> (<see cref="SftpPollOrgJob"/>) kept pulling files for orgs
///   whose plan does not include SFTP ingestion.</item>
///   <item><b>S3/R2 polling</b> (<see cref="S3PollOrgJob"/>) likewise.</item>
/// </list>
///
/// <para><b>The soft cap must survive.</b> REST ingress deliberately reuses
/// <c>CheckOrderLimitAsync</c> — the exact gate <c>OrdersController.Upload</c> uses —
/// because its <c>Allowed</c> flag is <c>BillingStatus.CanProcessOrders</c>, which is
/// <i>never</i> false for an active paid plan on volume grounds. Going over the monthly
/// allowance keeps working and accrues €0.50/order overage; only a non-processing account
/// status (or an expired Pilot) blocks. <see cref="RestIngress_OverTheSoftCap_OnAPaidPlan_StillAcceptsTheOrder"/>
/// pins that.</para>
/// </summary>
public class IngestPathBillingGateTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── REST ingress ─────────────────────────────────────────────────────────

    private sealed record IngressHarness(
        IngressController Controller,
        Guid OrgId,
        Supplier Supplier,
        Mock<IOrderService> Orders,
        Mock<IClaimedOrderCreator> ClaimedOrders,
        Mock<IBillingService> Billing);

    private static IngressHarness BuildIngress(LimitCheckResult limit)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();

        var org = new Organisation
        {
            Id = orgId,
            Name = "Acme",
            Slug = "acme",
            ClerkOrgId = "org_" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
        db.Organisations.Add(org);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Widgets Ltd", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(limit);

        // Every request in this file wins a FRESH claim (WP-22 claim-first dedupe), so the
        // billing gate is always reached. A replay (IsNew: false) short-circuits above the
        // gate on purpose — an already-accepted order must keep being returned even after
        // the account state changes — and that path is pinned by the dedupe suite, not here.
        var idempotency = new Mock<IIdempotencyService>();
        idempotency.Setup(i => i.ClaimAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new IdempotencyClaim(IsNew: true, OrderId: Guid.NewGuid()));

        var orders = new Mock<IOrderService>();

        // Order creation ALWAYS succeeds in this harness. That is deliberate: it makes the
        // gate the only thing under test, so a missing gate shows up as a 200 OK rather than
        // as an incidental crash further down the method.
        //
        // REST ingress creates through IClaimedOrderCreator, not IOrderService — the order
        // is written under the id the claim row already committed. The "never creates an
        // order" assertions verify against THIS mock for that reason; verifying the old
        // IOrderService method would pass without the gate, having asserted nothing.
        var claimedOrders = new Mock<IClaimedOrderCreator>();
        claimedOrders.Setup(o => o.CreateClaimedFromParsedOrderAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(),
                It.IsAny<ProcuLink.Core.Services.Ai.ExtractedOrder>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplier.Id,
                PoNumber = "PO-9001",
                Status = "pending_review",
            }));

        var controller = new IngressController(
            db, idempotency.Object, tenant.Object, billing.Object,
            NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };

        return new IngressHarness(controller, orgId, supplier, orders, claimedOrders, billing);
    }

    private static IngressOrderRequest OrderPayload(Guid supplierId) => new(
        OrderNumber: "PO-9001",
        OrderDate: new DateOnly(2026, 7, 30),
        Currency: "EUR",
        Notes: null,
        SupplierId: supplierId.ToString(),
        Lines: new List<IngressOrderLine>
        {
            new(BuyerItemCode: "ABC-1", Description: "Widget", Quantity: 2m, Unit: "EA", UnitPrice: 9.5m),
        });

    [Fact]
    public async Task RestIngress_FrozenOrg_IsRefused_AndNeverCreatesAnOrder()
    {
        // A cancelled / read-only ("frozen Pilot") org: CanProcessOrders is false.
        var h = BuildIngress(new LimitCheckResult(
            Allowed: false, PilotExpired: true, Plan: PlanConstants.Pilot, Limit: 20));

        var result = await h.Controller.ReceiveOrder(
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, h.ClaimedOrders.Object, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(429, "REST ingress must refuse exactly like the browser upload does");

        h.ClaimedOrders.Verify(o => o.CreateClaimedFromParsedOrderAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(),
            It.IsAny<ProcuLink.Core.Services.Ai.ExtractedOrder>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "a refused push must not create an order");
    }

    [Fact]
    public async Task RestIngress_FrozenOrg_ReturnsTheSameErrorShapeAsTheBrowserUpload()
    {
        var h = BuildIngress(new LimitCheckResult(
            Allowed: false, PilotExpired: true, Plan: PlanConstants.Pilot, Limit: 20));

        var result = await h.Controller.ReceiveOrder(
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, h.ClaimedOrders.Object, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        ((string)((dynamic)status.Value!).error).Should().Be("pilot_expired");
        ((string)((dynamic)status.Value!).upgradeUrl).Should().Be("/settings");
    }

    [Fact]
    public async Task RestIngress_OverTheSoftCap_OnAPaidPlan_StillAcceptsTheOrder()
    {
        // THE soft-cap invariant: a paid org over its monthly allowance is still Allowed
        // (CanProcessOrders ignores volume for active paid plans) — the overage is billed,
        // never blocked. If this ever fails, the new gate turned a soft cap into a hard one.
        var h = BuildIngress(new LimitCheckResult(
            Allowed: true, PilotExpired: false, Plan: PlanConstants.Growth, Limit: 150));

        var result = await h.Controller.ReceiveOrder(
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, h.ClaimedOrders.Object, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("going over a paid plan's cap accrues overage, never a block");
    }

    // ── SFTP / S3 pull jobs ──────────────────────────────────────────────────

    [Fact]
    public async Task SftpPollOrgJob_WithoutTheSftpIngestionFeature_DoesNotPoll()
    {
        var orgId = Guid.NewGuid();
        var sftp = new Mock<ISftpIngressService>();
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var job = new SftpPollOrgJob(sftp.Object, billing.Object, NullLogger<SftpPollOrgJob>.Instance);

        await job.ExecuteAsync(orgId, CancellationToken.None);

        sftp.Verify(s => s.PollAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "the IMAP poller has always early-returned on a missing feature; SFTP must too");
    }

    [Fact]
    public async Task SftpPollOrgJob_WithTheFeature_StillPolls()
    {
        var orgId = Guid.NewGuid();
        var sftp = new Mock<ISftpIngressService>();
        sftp.Setup(s => s.PollAsync(orgId, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var job = new SftpPollOrgJob(sftp.Object, billing.Object, NullLogger<SftpPollOrgJob>.Instance);

        await job.ExecuteAsync(orgId, CancellationToken.None);

        sftp.Verify(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task S3PollOrgJob_WithoutTheS3IngestionFeature_DoesNotPoll()
    {
        var orgId = Guid.NewGuid();
        var s3 = new Mock<IS3IngressService>();
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.S3Ingestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var job = new S3PollOrgJob(s3.Object, billing.Object, NullLogger<S3PollOrgJob>.Instance);

        await job.ExecuteAsync(orgId, CancellationToken.None);

        s3.Verify(s => s.PollAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task S3PollOrgJob_WithTheFeature_StillPolls()
    {
        var orgId = Guid.NewGuid();
        var s3 = new Mock<IS3IngressService>();
        s3.Setup(s => s.PollAsync(orgId, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.S3Ingestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var job = new S3PollOrgJob(s3.Object, billing.Object, NullLogger<S3PollOrgJob>.Instance);

        await job.ExecuteAsync(orgId, CancellationToken.None);

        s3.Verify(s => s.PollAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The gate must run BEFORE any network work — a plan that does not include the
    /// channel should never open a connection to the customer's server at all.
    /// </summary>
    [Fact]
    public async Task PullJobs_AreIdempotentWhenGated_RepeatRunsStayNoOps()
    {
        var orgId = Guid.NewGuid();
        var sftp = new Mock<ISftpIngressService>();
        var s3 = new Mock<IS3IngressService>();
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var sftpJob = new SftpPollOrgJob(sftp.Object, billing.Object, NullLogger<SftpPollOrgJob>.Instance);
        var s3Job = new S3PollOrgJob(s3.Object, billing.Object, NullLogger<S3PollOrgJob>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await sftpJob.ExecuteAsync(orgId, CancellationToken.None);
            await s3Job.ExecuteAsync(orgId, CancellationToken.None);
        }

        sftp.Verify(s => s.PollAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(s => s.PollAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Inbound email (Postmark PUSH webhook) ────────────────────────────────
    //
    // B-8, the fourth path. The IMAP PULL channel has re-checked EmailIngestion every cycle since
    // it shipped; the PUSH webhook checked NOTHING — no plan gate and no order-limit gate — and the
    // inbound address is auto-minted for every org on every plan. A comment on the router asserted
    // that "monthly limits are enforced downstream by ParseOrderJob"; that file contains no billing
    // code of any kind, and the comment is why nobody looked. Pilot's 20-order cap is the model's
    // only HARD cap, and email walked around it.
    //
    // B-9 lived in the same method: a hand-written 2-of-4 copy of the read-only status set, missing
    // past_due and cancelled, so a delinquent org kept ingesting by email — and kept being billed —
    // while every other path refused it.

    private const string EmailToken = "acme-inbound";

    private sealed record EmailHarness(
        InboundEmailRouter Router,
        Guid OrgId,
        Mock<IClaimedOrderCreator> Orders,
        Mock<IBillingService> Billing);

    /// <summary>
    /// A router over a seeded org with a live inbound address. Order creation ALWAYS succeeds, for
    /// the same reason the ingress harness does it: the gate is then the only thing under test, so a
    /// missing gate shows up as an order being created rather than as an incidental crash.
    /// </summary>
    private static EmailHarness BuildEmail(
        bool hasEmailIngestion,
        LimitCheckResult limit,
        string accountStatus = AccountStatusConstants.Active)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            Name = "Acme",
            Slug = "acme",
            ClerkOrgId = "org_" + Guid.NewGuid().ToString("N"),
            AccountStatus = accountStatus,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var config = InboundAddressTestHarness.Configuration();
        InboundAddressTestHarness.SeedAddress(db, orgId, EmailToken, config);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.EmailIngestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(hasEmailIngestion);
        billing.Setup(b => b.CheckOrderLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(limit);

        var orders = new Mock<IClaimedOrderCreator>();
        orders.Setup(o => o.CreateClaimedStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid org, Guid? sup, Guid id, Stream _, string _, string _, string? _, CancellationToken _) =>
                Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
                {
                    Id = id, OrgId = org, SupplierId = sup, PoNumber = "PO-EMAIL-1", Status = "parsing",
                }));

        var router = new InboundEmailRouter(
            db, orders.Object, new NoOpEnqueuer(), NoOrderBody.Instance,
            InboundAddressTestHarness.Create(db, config),
            billing.Object, config,
            NullLogger<InboundEmailRouter>.Instance);

        return new EmailHarness(router, orgId, orders, billing);
    }

    private static InboundEmailPayload EmailPayload() => new(
        FromEmail: "buyer@heinrich.example.com",
        ToEmail: $"orders@{EmailToken}.proculink.eu",
        Subject: "PO attached",
        Attachments: new[]
        {
            new InboundAttachment("po.csv", "text/csv", System.Text.Encoding.UTF8.GetBytes("po,qty\r\nB8-1,4\r\n")),
        });

    private static void VerifyNoOrderCreated(EmailHarness h, string because) =>
        h.Orders.Verify(o => o.CreateClaimedStubAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, because);

    /// <summary>
    /// The anti-vacuity control for every "never creates an order" assertion below: a healthy org on
    /// a plan that includes email ingestion DOES get an order out of this harness. Without this, a
    /// harness that never created one would make all four refusal tests pass having proved nothing.
    /// </summary>
    [Fact]
    public async Task InboundEmail_HealthyOrgOnAPlanWithEmailIngestion_CreatesTheOrder()
    {
        var h = BuildEmail(hasEmailIngestion: true, new LimitCheckResult(
            Allowed: true, PilotExpired: false, Plan: PlanConstants.Growth, Limit: 150));

        var result = await h.Router.RouteAsync(EmailPayload(), CancellationToken.None);

        result.CreatedOrderIds.Should().HaveCount(1,
            "the harness must be able to create an order, or every refusal assertion below is vacuous");
    }

    /// <summary>
    /// <i>"I'm on the free Pilot and I've put 400 orders through by email."</i>
    /// </summary>
    [Fact]
    public async Task InboundEmail_WithTheOrderAllowanceExhausted_IsRefused_AndNeverCreatesAnOrder()
    {
        var h = BuildEmail(hasEmailIngestion: true, new LimitCheckResult(
            Allowed: false, PilotExpired: true, Plan: PlanConstants.Pilot, Limit: 20));

        var result = await h.Router.RouteAsync(EmailPayload(), CancellationToken.None);

        result.Success.Should().BeFalse("Pilot's cap is the billing model's only HARD cap");
        result.CreatedOrderIds.Should().BeEmpty();
        VerifyNoOrderCreated(h, "an exhausted allowance must not ingest by email either");
    }

    [Fact]
    public async Task InboundEmail_OnAPlanWithoutEmailIngestion_IsRefused_AndNeverCreatesAnOrder()
    {
        // The address exists — it is auto-minted for every org on every plan — but the plan does
        // not include the channel. IMAP has refused this since it shipped.
        var h = BuildEmail(hasEmailIngestion: false, new LimitCheckResult(
            Allowed: true, PilotExpired: false, Plan: PlanConstants.Pilot, Limit: 20));

        var result = await h.Router.RouteAsync(EmailPayload(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CreatedOrderIds.Should().BeEmpty();
        VerifyNoOrderCreated(h, "a plan without EmailIngestion must not ingest by email");
    }

    /// <summary>
    /// B-9. Permissive billing on purpose: these two statuses must be refused by the router's OWN
    /// status gate, which is what the stale 2-of-4 local copy failed to do. A test that let billing
    /// refuse them instead would pass with the old set still in place.
    /// </summary>
    [Theory]
    [InlineData(AccountStatusConstants.PastDue)]
    [InlineData(AccountStatusConstants.Cancelled)]
    [InlineData(AccountStatusConstants.ReadOnly)]
    [InlineData(AccountStatusConstants.TrialExpired)]
    public async Task InboundEmail_ForAReadOnlyAccountStatus_IsRefused_AndNeverCreatesAnOrder(string status)
    {
        var h = BuildEmail(
            hasEmailIngestion: true,
            new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: PlanConstants.Growth, Limit: 150),
            accountStatus: status);

        var result = await h.Router.RouteAsync(EmailPayload(), CancellationToken.None);

        result.Success.Should().BeFalse(
            "every other ingest path refuses '{0}' via AccountStatusConstants.IsReadOnly", status);
        result.CreatedOrderIds.Should().BeEmpty();
        VerifyNoOrderCreated(h, $"'{status}' blocks ingest on every other path");
    }

    /// <summary>
    /// THE soft-cap invariant, restated for this channel: a paid org over its monthly allowance is
    /// still Allowed, because CanProcessOrders ignores volume for an active paid plan. The overage
    /// is billed, never blocked. If this fails, the new gate turned a soft cap into a hard one.
    /// </summary>
    [Fact]
    public async Task InboundEmail_OverTheSoftCap_OnAPaidPlan_StillCreatesTheOrder()
    {
        var h = BuildEmail(hasEmailIngestion: true, new LimitCheckResult(
            Allowed: true, PilotExpired: false, Plan: PlanConstants.Growth, Limit: 150));

        var result = await h.Router.RouteAsync(EmailPayload(), CancellationToken.None);

        result.CreatedOrderIds.Should().HaveCount(1,
            "going over a paid plan's cap accrues overage, never a block");
    }

    /// <summary>
    /// Every billing refusal must be TRANSIENT. A Permanent rejection answers Postmark 200, which
    /// ends re-delivery for good — so a purchase order held back by a reversible billing state
    /// would be lost rather than delayed. Transient keeps it re-fireable for ~10.5 hours.
    /// </summary>
    [Fact]
    public async Task InboundEmail_BillingRefusals_AreTransient_SoTheOrderSurvivesTheFix()
    {
        var overCap = await BuildEmail(hasEmailIngestion: true, new LimitCheckResult(
            Allowed: false, PilotExpired: true, Plan: PlanConstants.Pilot, Limit: 20))
            .Router.RouteAsync(EmailPayload(), CancellationToken.None);

        var noFeature = await BuildEmail(hasEmailIngestion: false, new LimitCheckResult(
            Allowed: true, PilotExpired: false, Plan: PlanConstants.Pilot, Limit: 20))
            .Router.RouteAsync(EmailPayload(), CancellationToken.None);

        overCap.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient);
        noFeature.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient);
    }

    private sealed class NoOpEnqueuer : IParseJobEnqueuer
    {
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOrderBody : ProcuLink.Core.Services.Ai.IEmailBodyOrderExtractor
    {
        public static readonly NoOrderBody Instance = new();
        public Task<ProcuLink.Core.Services.Ai.EmailBodyExtractionResult> ExtractAsync(
            string emailBody, CancellationToken ct) =>
            Task.FromResult(new ProcuLink.Core.Services.Ai.EmailBodyExtractionResult(
                Success: false, Confidence: 0, Order: null, FailureReason: "test double"));
    }
}
