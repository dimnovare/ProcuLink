using FluentAssertions;
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
/// Finding 8.2 dispatch logic in <see cref="BillingController.HandleInvoiceCreatedAsync"/>:
/// the overage item is attached to the triggering invoice ONLY while it is still a
/// DRAFT (invoice.created fires ~1h before finalisation); any other status falls
/// back to the historical customer-sweep call so a late replay can never 400 on
/// attaching to a finalised invoice. Idempotency keys are identical in both paths.
/// </summary>
public class OverageInvoiceAttachDispatchTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (BillingController Ctrl, Mock<IBillingService> Billing, Organisation Org, ProcuLinkDbContext Db)
    BuildWithOrg(string stripeCustomerId)
    {
        var db = MakeDb();
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id               = id,
            ClerkOrgId       = $"org_{id:N}",
            Name             = "Dispatch Org",
            Slug             = $"dispatch-{id:N}",
            Plan             = PlanConstants.Operations,
            AccountStatus    = AccountStatusConstants.Active,
            StripeCustomerId = stripeCustomerId,
            CreatedAt        = DateTime.UtcNow.AddDays(-30),
            TrialStartedAt   = DateTime.UtcNow.AddDays(-30),
        };
        db.Organisations.Add(org);
        db.SaveChanges();

        var billing = new Mock<IBillingService>();
        var ctrl = new BillingController(
            billing.Object,
            Mock.Of<ICurrentTenantService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<BillingController>.Instance,
            db,
            new Mock<IAiUsageTracker>().Object);
        return (ctrl, billing, org, db);
    }

    private static Stripe.Invoice MakeSubscriptionInvoice(
        string invoiceId, string customerId, string? status, DateTime periodStart, DateTime periodEnd) =>
        new()
        {
            Id          = invoiceId,
            CustomerId  = customerId,
            Status      = status,
            PeriodStart = periodStart,
            PeriodEnd   = periodEnd,
            Parent      = new Stripe.InvoiceParent
            {
                SubscriptionDetails = new Stripe.InvoiceParentSubscriptionDetails
                {
                    SubscriptionId = "sub_dispatch",
                },
            },
        };

    private static readonly DateTime May1  = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun1  = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DraftInvoice_BillsThroughTheAttachOverload_WithTheInvoiceId()
    {
        var (ctrl, billing, org, _) = BuildWithOrg("cus_draft");
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                org.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        billing
            .Setup(b => b.BillOverageForInvoiceAsync(
                org.Id, It.IsAny<string>(), 7, "in_draft_attach", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid o, string key, int orders, string _, CancellationToken _) =>
                new OverageBillingResult(o, key, orders, orders * 50L, AlreadyBilled: false, StripeItemId: "ii_attach"));

        var invoice = MakeSubscriptionInvoice("in_draft_attach", "cus_draft", "draft", May1, Jun1);
        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        var expectedKey = BillingController.BuildPeriodBillingKey(org.Id, May1);
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            org.Id, expectedKey, 7, "in_draft_attach", It.IsAny<CancellationToken>()), Times.Once,
            "a draft invoice must take the ATTACH overload with the invoice id and the unchanged period key");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "the customer-sweep overload must not also be called");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("paid")]
    [InlineData("void")]
    [InlineData(null)] // status missing on the event payload → not provably a draft
    public async Task NonDraftInvoice_FallsBackToTheCustomerSweepOverload(string? status)
    {
        var (ctrl, billing, org, _) = BuildWithOrg("cus_nondraft");
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                org.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        billing
            .Setup(b => b.BillOverageForInvoiceAsync(
                org.Id, It.IsAny<string>(), 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid o, string key, int orders, CancellationToken _) =>
                new OverageBillingResult(o, key, orders, orders * 50L, AlreadyBilled: false, StripeItemId: "ii_sweep"));

        var invoice = MakeSubscriptionInvoice("in_nondraft", "cus_nondraft", status, May1, Jun1);
        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        var expectedKey = BillingController.BuildPeriodBillingKey(org.Id, May1);
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            org.Id, expectedKey, 3, It.IsAny<CancellationToken>()), Times.Once,
            "a non-draft invoice must keep the historical customer-sweep call (same idempotency key)");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "attaching to a non-draft invoice would 400 at Stripe — must not be attempted");
    }

    [Fact]
    public async Task DraftYearlyInvoice_AttachesEveryMonthlyWindowItem_ToTheSameRenewalInvoice()
    {
        var (ctrl, billing, org, _) = BuildWithOrg("cus_yearly_draft");
        var jan1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jan1Next = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Two months over cap; the rest at zero.
        var mar1 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var aug1 = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        billing
            .Setup(b => b.ComputePeriodOverageOrdersAsync(
                org.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTime s, DateTime _, CancellationToken _) =>
                s == mar1 ? 4 : s == aug1 ? 6 : 0);
        billing
            .Setup(b => b.BillOverageForInvoiceAsync(
                org.Id, It.IsAny<string>(), It.IsAny<int>(), "in_yearly_draft", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid o, string key, int orders, string _, CancellationToken _) =>
                new OverageBillingResult(o, key, orders, orders * 50L, AlreadyBilled: false, StripeItemId: "ii_y"));

        var invoice = MakeSubscriptionInvoice("in_yearly_draft", "cus_yearly_draft", "draft", jan1, jan1Next);
        await ctrl.HandleInvoiceCreatedAsync(invoice, CancellationToken.None);

        billing.Verify(b => b.BillOverageForInvoiceAsync(
            org.Id, BillingController.BuildPeriodBillingKey(org.Id, mar1), 4, "in_yearly_draft", It.IsAny<CancellationToken>()),
            Times.Once, "March's overage item attaches to the renewal invoice");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            org.Id, BillingController.BuildPeriodBillingKey(org.Id, aug1), 6, "in_yearly_draft", It.IsAny<CancellationToken>()),
            Times.Once, "August's overage item attaches to the same renewal invoice");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "only the over-cap months are billed");
        billing.Verify(b => b.BillOverageForInvoiceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "no window may fall back to the customer sweep while the invoice is a draft");
    }
}
