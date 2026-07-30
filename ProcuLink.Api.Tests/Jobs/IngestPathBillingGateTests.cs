using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
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

        var idempotency = new Mock<IIdempotencyService>();
        idempotency.Setup(i => i.TryGetExistingOrderIdAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        // Order creation ALWAYS succeeds in this harness. That is deliberate: it makes the
        // gate the only thing under test, so a missing gate shows up as a 200 OK rather than
        // as an incidental crash further down the method.
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.CreateStubFromParsedOrderAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProcuLink.Core.Services.Ai.ExtractedOrder>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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

        return new IngressHarness(controller, orgId, supplier, orders, billing);
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
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(429, "REST ingress must refuse exactly like the browser upload does");

        h.Orders.Verify(o => o.CreateStubFromParsedOrderAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProcuLink.Core.Services.Ai.ExtractedOrder>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never,
            "a refused push must not create an order");
    }

    [Fact]
    public async Task RestIngress_FrozenOrg_ReturnsTheSameErrorShapeAsTheBrowserUpload()
    {
        var h = BuildIngress(new LimitCheckResult(
            Allowed: false, PilotExpired: true, Plan: PlanConstants.Pilot, Limit: 20));

        var result = await h.Controller.ReceiveOrder(
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, CancellationToken.None);

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
            "acme", OrderPayload(h.Supplier.Id), h.Orders.Object, CancellationToken.None);

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
}
