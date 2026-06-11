using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Delivered-only billing meter tests (<c>Billing:CountDeliveredOnly</c>, default OFF):
///  • flag OFF (absent or garbage value) keeps the historical count-at-creation meter —
///    every non-sample order counts regardless of status (byte-identical default);
///  • flag ON counts ONLY orders that reached the supplier: <c>delivered</c> +
///    <c>rejected_by_supplier</c> (rejection = supplier business outcome AFTER the document
///    was delivered); parsing/review/failed/dead-letter/delivering are excluded — on both
///    the Pilot-cumulative and paid-monthly quota branches AND the overage window meter;
///  • creation-month attribution under the flag: an order belongs to its CreatedAt window
///    and is counted there once delivered (counted at count time); it never leaks into the
///    delivery month's window;
///  • sample-order exclusion holds in both modes.
/// Stripe is intentionally unconfigured (no SecretKey) so no live HTTP is made.
/// </summary>
public class StripeBillingServiceDeliveredMeterTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Service with the delivered-only flag set to the given raw config value (null = key absent).</summary>
    private static StripeBillingService MakeService(ProcuLinkDbContext db, string? deliveredOnlyFlag)
    {
        var settings = new Dictionary<string, string?>(); // no Stripe:SecretKey → no live HTTP
        if (deliveredOnlyFlag is not null)
            settings[StripeBillingService.CountDeliveredOnlyFlagKey] = deliveredOnlyFlag;

        return new StripeBillingService(
            db,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService());
    }

    private static Organisation Org(string plan, string status)
    {
        var id = Guid.NewGuid();
        return new Organisation
        {
            Id             = id,
            ClerkOrgId     = $"org_{id:N}",
            Name           = "Test Org",
            Slug           = $"test-{id:N}",
            Plan           = plan,
            AccountStatus  = status,
            CreatedAt      = DateTime.UtcNow.AddDays(-10),
            TrialStartedAt = DateTime.UtcNow.AddDays(-10),
        };
    }

    private static Guid SeedSupplier(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Sup", CreatedAt = DateTime.UtcNow });
        return supplierId;
    }

    /// <summary>Adds <paramref name="count"/> orders with the given status and CreatedAt base.</summary>
    private static void SeedOrders(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        int count, string status, DateTime createdAtBase, bool isSample = false)
    {
        for (var i = 0; i < count; i++)
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                SupplierId = supplierId,
                PoNumber   = $"PO-{Guid.NewGuid():N}",
                Status     = status,
                OrderDate  = DateOnly.FromDateTime(createdAtBase),
                Currency   = "EUR",
                CreatedAt  = createdAtBase.AddSeconds(i + 1),
                UpdatedAt  = createdAtBase.AddSeconds(i + 1),
                IsSample   = isSample,
            });
        }
    }

    private static DateTime ThisMonthStart() =>
        new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── flag OFF: byte-identical count-at-creation meter ──────────────────

    [Theory]
    [InlineData(null)]      // key absent — the production default
    [InlineData("false")]
    [InlineData("banana")]  // unparseable ⇒ OFF (TryParse pattern)
    public async Task FlagOff_PaidMonthly_CountsEveryNonSampleOrderRegardlessOfStatus(string? flagValue)
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Delivered,          monthStart);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Parsing,            monthStart.AddMinutes(10));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryFailed,     monthStart.AddMinutes(20));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.PendingReview,      monthStart.AddMinutes(30));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.RejectedBySupplier, monthStart.AddMinutes(40));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Delivered,          monthStart.AddMinutes(50), isSample: true);
        await db.SaveChangesAsync();

        var status = await MakeService(db, flagValue).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(8,
            "flag OFF counts every non-sample order at creation regardless of status (sample excluded)");
    }

    // ── flag ON: paid-monthly quota counts only delivered + rejected ──────

    [Fact]
    public async Task FlagOn_PaidMonthly_CountsOnlyDeliveredAndRejectedBySupplier()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Delivered,          monthStart);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.RejectedBySupplier, monthStart.AddMinutes(10));
        SeedOrders(db, org.Id, sup, 4, OrderStatusConstants.Parsing,            monthStart.AddMinutes(20));
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.DeliveryFailed,     monthStart.AddMinutes(30));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.PendingReview,      monthStart.AddMinutes(40));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryDeadLetter, monthStart.AddMinutes(50));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Delivering,         monthStart.AddMinutes(60));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Failed,             monthStart.AddMinutes(70));
        await db.SaveChangesAsync();

        var status = await MakeService(db, "true").GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(5,
            "flag ON counts only delivered (3) + rejected_by_supplier (2); " +
            "parsing/failed/review/dead-letter/delivering never reached the supplier");
    }

    // ── flag ON: Pilot cumulative branch uses the same filter ─────────────

    [Fact]
    public async Task FlagOn_PilotCumulative_CountsOnlyDeliveredAndRejectedBySupplier()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var sinceTrial = org.TrialStartedAt.AddDays(1);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Delivered,          sinceTrial);
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.RejectedBySupplier, sinceTrial.AddMinutes(10));
        SeedOrders(db, org.Id, sup, 5, OrderStatusConstants.Parsing,            sinceTrial.AddMinutes(20));
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.DeliveryFailed,     sinceTrial.AddMinutes(30));
        await db.SaveChangesAsync();

        var status = await MakeService(db, "true").GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(3,
            "the Pilot cumulative branch applies the same delivered-only filter: 2 delivered + 1 rejected");
    }

    // ── sample exclusion holds in BOTH modes ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public async Task SampleOrders_AreExcluded_InBothMeterModes(string? flagValue)
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Delivered, monthStart);
        // Delivered SAMPLE orders — must never count, even though they pass the status filter.
        SeedOrders(db, org.Id, sup, 4, OrderStatusConstants.Delivered, monthStart.AddMinutes(10), isSample: true);
        await db.SaveChangesAsync();

        var status = await MakeService(db, flagValue).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(2, "sample orders never count against quota in either meter mode");
    }

    // ── overage window meter (the invoice.created path per monthly window) ─

    [Fact]
    public async Task FlagOn_Overage_ExcludesUndeliveredOrdersFromTheWindow()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 153, OrderStatusConstants.Delivered,      monthStart); // 3 over the cap
        SeedOrders(db, org.Id, sup, 10,  OrderStatusConstants.Parsing,        monthStart.AddHours(1));
        SeedOrders(db, org.Id, sup, 5,   OrderStatusConstants.DeliveryFailed, monthStart.AddHours(2));
        await db.SaveChangesAsync();

        var windowEnd = DateTime.UtcNow.AddDays(1);
        var flagOn  = await MakeService(db, "true").ComputePeriodOverageOrdersAsync(org.Id, monthStart, windowEnd);
        var flagOff = await MakeService(db, null).ComputePeriodOverageOrdersAsync(org.Id, monthStart, windowEnd);

        flagOn.Should().Be(3, "only the 153 delivered orders count ⇒ 3 over the 150 cap; parsing/failed are free");
        flagOff.Should().Be(18, "flag OFF counts all 168 orders at creation ⇒ 18 over the cap (unchanged default)");
    }

    [Fact]
    public async Task FlagOn_Overage_RejectedBySupplier_CountsAsDelivered()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 149, OrderStatusConstants.Delivered,          monthStart);
        SeedOrders(db, org.Id, sup, 3,   OrderStatusConstants.RejectedBySupplier, monthStart.AddHours(1));
        await db.SaveChangesAsync();

        var overage = await MakeService(db, "true").ComputePeriodOverageOrdersAsync(
            org.Id, monthStart, DateTime.UtcNow.AddDays(1));

        overage.Should().Be(2,
            "rejected_by_supplier reached the supplier (work performed + delivered; rejection is the " +
            "supplier's business outcome) ⇒ 149 + 3 = 152 counted ⇒ 2 over the 150 cap");
    }

    // ── creation-month attribution under the flag ─────────────────────────
    // An order is attributed to the window containing its CreatedAt; the status filter is
    // evaluated AT COUNT TIME. Deterministic variant of the late-delivery rule: an order
    // created in month M is counted in M's window once delivered (when the count runs at or
    // before M's invoice), and NEVER appears in the delivery month's window. If M's invoice
    // was already issued before delivery, the order is simply never billed — customer-
    // favorable, no catch-up (documented in ComputePeriodOverageOrdersAsync).

    [Fact]
    public async Task FlagOn_CreationMonthAttribution_LateDeliveredOrderCountsInItsCreationMonthOnly()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);

        var thisMonthStart = ThisMonthStart();
        var lastMonthStart = thisMonthStart.AddMonths(-1);

        // 151 delivered orders created LAST month + 1 order created last month still in
        // flight ("PO-LATE", not yet delivered when the period closes).
        SeedOrders(db, org.Id, sup, 151, OrderStatusConstants.Delivered, lastMonthStart);
        var late = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = org.Id,
            SupplierId = sup,
            PoNumber   = "PO-LATE",
            Status     = OrderStatusConstants.Delivering, // undelivered at invoice time
            OrderDate  = DateOnly.FromDateTime(lastMonthStart),
            Currency   = "EUR",
            CreatedAt  = lastMonthStart.AddHours(5),
            UpdatedAt  = lastMonthStart.AddHours(5),
        };
        db.PurchaseOrders.Add(late);
        await db.SaveChangesAsync();

        var svc = MakeService(db, "true");

        // Counted at the period close, before the late delivery: only the 151 delivered.
        (await svc.ComputePeriodOverageOrdersAsync(org.Id, lastMonthStart, thisMonthStart))
            .Should().Be(1, "151 delivered in the creation month ⇒ 1 over the 150 cap; PO-LATE is still in flight");

        // The order is delivered later (this month). Recounting the CREATION month now
        // includes it — count(month=M) includes the order once delivered, because the
        // status filter is evaluated at count time while attribution stays CreatedAt-based.
        late.Status    = OrderStatusConstants.Delivered;
        late.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        (await svc.ComputePeriodOverageOrdersAsync(org.Id, lastMonthStart, thisMonthStart))
            .Should().Be(2, "once delivered, PO-LATE counts in its CREATION month's window (152 ⇒ 2 over)");

        // It never leaks into the DELIVERY month's window — creation-month attribution.
        (await svc.ComputePeriodOverageOrdersAsync(org.Id, thisMonthStart, DateTime.UtcNow.AddDays(1)))
            .Should().Be(0, "no order was CREATED this month; the late delivery must not re-enter a later window");
    }

    // ── flag ON: quota status DTO consistency (limit flags follow the filtered count) ──

    [Fact]
    public async Task FlagOn_OverLimitFlags_FollowTheDeliveredCount()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // limit 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        // 160 created, only 100 delivered: flag ON must NOT report the org over-limit.
        SeedOrders(db, org.Id, sup, 100, OrderStatusConstants.Delivered, monthStart);
        SeedOrders(db, org.Id, sup, 60,  OrderStatusConstants.Parsing,   monthStart.AddHours(1));
        await db.SaveChangesAsync();

        var status = await MakeService(db, "true").GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(100);
        status.IsOrderLimitReached.Should().BeFalse("100 delivered < 150 — in-flight orders don't trip the cap");
        status.AtLimit.Should().BeFalse();
        status.OverageOrders.Should().Be(0);
        status.CanProcessOrders.Should().BeTrue();
    }
}
