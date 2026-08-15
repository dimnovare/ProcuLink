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
/// A-F3 (2026-08-14 readiness audit): at the shipped default of
/// <c>Billing:CountDeliveredOnly</c> (absent in BOTH production Railway services, so OFF),
/// every FAILED order spent one of a Pilot's hard 20 — and no client-side order delete
/// exists, so the allowance could never be reclaimed. Five malformed uploads in the first
/// hour of a trial burned a quarter of it.
///
/// <para>The fix is asymmetric ON PURPOSE and these tests pin both halves:</para>
/// <list type="bullet">
///   <item><b>Pilot (HARD cap)</b> forgives the statuses where ProcuLink failed to produce a
///     delivery — <see cref="OrderStatusConstants.FailureBucket"/> minus
///     <see cref="OrderStatusConstants.RejectedBySupplier"/>. Over-counting a hard cap denies
///     service and cannot be undone.</item>
///   <item><b>Paid (SOFT cap)</b> is byte-identical to before: failures still count, the
///     invoiced overage window is untouched, and going over still never blocks. Over-counting
///     a soft cap bills €0.50 a customer can dispute — a change here would move MONEY and
///     falsify the published pricing/Terms copy, so it is deliberately not made.</item>
/// </list>
///
/// <para>Anti-vacuity: <see cref="PilotHardCap_StaysHard_TwentyDeliveredOrdersEndTheTrial"/>
/// proves the cap still terminates a trial, and
/// <see cref="PilotHardCap_ParkedAndInFlightOrders_StillCount"/> proves the forgiveness is
/// narrow — an order a human can still act on is NOT forgiven (that is the missing
/// order-delete affordance's job, tracked separately).</para>
///
/// Stripe is intentionally unconfigured (no SecretKey) so no live HTTP is made.
/// </summary>
public class PilotHardCapForgivesFailedOrdersTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Service with the delivered-only flag at the given raw value (null = key absent = production default).</summary>
    private static StripeBillingService MakeService(ProcuLinkDbContext db, string? deliveredOnlyFlag = null)
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
            CreatedAt      = DateTime.UtcNow.AddDays(-3),
            TrialStartedAt = DateTime.UtcNow.AddDays(-3), // well inside the 14-day window
        };
    }

    private static Guid SeedSupplier(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Sup", CreatedAt = DateTime.UtcNow });
        return supplierId;
    }

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

    /// <summary>
    /// Every declared failure status, walked from the registry rather than hand-listed, so a
    /// sixth failure status added to <see cref="OrderStatusConstants.FailureBucket"/> is
    /// exercised here on the day it is declared instead of silently defaulting to "counts".
    /// </summary>
    public static TheoryData<string> EveryFailureStatus()
    {
        var data = new TheoryData<string>();
        foreach (var status in OrderStatusConstants.FailureBucket.OrderBy(s => s, StringComparer.Ordinal))
            data.Add(status);
        return data;
    }

    // ── the finding itself ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]      // key absent — the PRODUCTION default (verified on Railway 2026-08-14)
    [InlineData("false")]
    [InlineData("banana")]  // unparseable ⇒ OFF (TryParse pattern)
    public async Task PilotHardCap_FailedOrders_DoNotSpendTheTrialAllowance(string? flagValue)
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing); // hard cap 20
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var t0 = org.TrialStartedAt.AddHours(1);

        // The trial's first hour: five malformed uploads, then three real orders delivered.
        SeedOrders(db, org.Id, sup, 5, OrderStatusConstants.Failed,    t0);
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Delivered, t0.AddMinutes(10));
        await db.SaveChangesAsync();

        var status = await MakeService(db, flagValue).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(3,
            "an order ProcuLink could not process was never an order — only the 3 delivered spend the trial");
        status.OrderLimit.Should().Be(PlanConstants.PilotOrderLimit);
        status.IsTrialExpired.Should().BeFalse();
        status.CanProcessOrders.Should().BeTrue("17 of the 20 are still available");
    }

    [Theory]
    [MemberData(nameof(EveryFailureStatus))]
    public async Task PilotHardCap_ForgivesEveryFailureStatus_ExceptRejectedBySupplier(string failureStatus)
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        SeedOrders(db, org.Id, sup, 4, failureStatus, org.TrialStartedAt.AddHours(1));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        var reachedTheSupplier = failureStatus == OrderStatusConstants.RejectedBySupplier;
        status.OrdersThisMonth.Should().Be(reachedTheSupplier ? 4 : 0,
            reachedTheSupplier
                ? "a rejection means the document WAS delivered — the full parse/transform/delivery " +
                  "work was performed and the supplier declined it as a business decision"
                : $"'{failureStatus}' never reached the supplier, so it must not spend the hard cap");
    }

    [Fact]
    public async Task PilotHardCap_ParkedAndInFlightOrders_StillCount()
    {
        // Narrowness pin. Only FAILED orders are forgiven. An order a human can still act on
        // (review / unrouted / unconfirmed) or that a job still holds (parsing / delivering)
        // is real work in the workspace and keeps counting — reclaiming those is the missing
        // order-delete affordance's job, not the meter's.
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var t0 = org.TrialStartedAt.AddHours(1);
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.PendingReview,       t0);
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Unrouted,            t0.AddMinutes(1));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryUnconfirmed, t0.AddMinutes(2));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Parsing,             t0.AddMinutes(3));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.Delivering,          t0.AddMinutes(4));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryHeld,        t0.AddMinutes(5));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(6,
            "parked, in-flight and billing-held orders are not failures and still spend the trial allowance");
    }

    [Fact]
    public async Task PilotHardCap_StaysHard_TwentyDeliveredOrdersEndTheTrial()
    {
        // Anti-vacuity: the forgiveness must not turn the one HARD cap in the product soft.
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        SeedOrders(db, org.Id, sup, PlanConstants.PilotOrderLimit, OrderStatusConstants.Delivered,
            org.TrialStartedAt.AddHours(1));
        SeedOrders(db, org.Id, sup, 7, OrderStatusConstants.Failed, org.TrialStartedAt.AddHours(2));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(PlanConstants.PilotOrderLimit, "the 7 failures are forgiven, the 20 are not");
        status.IsTrialExpired.Should().BeTrue("20 real orders is the whole trial");
        status.CanProcessOrders.Should().BeFalse("a trial-ended Pilot is read-only, by design");
        (await db.Organisations.AsNoTracking().FirstAsync(o => o.Id == org.Id)).AccountStatus
            .Should().Be(AccountStatusConstants.TrialExpired);
    }

    [Fact]
    public async Task PilotHardCap_ForgivenessFlowsThroughExpiry_ReactivatingAnOrgTippedOverByFailures()
    {
        // MarkPilotExpiredIfNeededAsync shares CountOrdersAsync, so an org already persisted as
        // trial_expired purely because failures were counted is reactivated on the next status read.
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.TrialExpired);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var t0 = org.TrialStartedAt.AddHours(1);
        SeedOrders(db, org.Id, sup, 15, OrderStatusConstants.Delivered, t0);
        SeedOrders(db, org.Id, sup, 5,  OrderStatusConstants.TransformFailed, t0.AddMinutes(30));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(15);
        status.IsTrialExpired.Should().BeFalse();
        status.CanProcessOrders.Should().BeTrue("the 5 transform failures never should have ended the trial");
        (await db.Organisations.AsNoTracking().FirstAsync(o => o.Id == org.Id)).AccountStatus
            .Should().Be(AccountStatusConstants.Trialing, "the expiry path reads the same forgiven count");
    }

    [Fact]
    public async Task PilotHardCap_UnderTheDeliveredOnlyFlag_IsUnchanged()
    {
        // Forgiveness is a strict subset of delivered-only, so flipping the flag ON is a no-op
        // for Pilot. Nobody has to reason about the two rules interacting.
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var t0 = org.TrialStartedAt.AddHours(1);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Delivered,          t0);
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.RejectedBySupplier, t0.AddMinutes(1));
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Failed,             t0.AddMinutes(2));
        await db.SaveChangesAsync();

        var flagOn = await MakeService(db, "true").GetStatusAsync(org.Id);
        flagOn.OrdersThisMonth.Should().Be(3, "delivered-only already excludes every forgiven failure");
    }

    [Fact]
    public async Task PilotHardCap_SampleOrdersAndForgivenessCompose()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Pilot, AccountStatusConstants.Trialing);
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var t0 = org.TrialStartedAt.AddHours(1);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Delivered, t0);
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Delivered, t0.AddMinutes(1), isSample: true);
        SeedOrders(db, org.Id, sup, 4, OrderStatusConstants.Failed,    t0.AddMinutes(2));
        await db.SaveChangesAsync();

        (await MakeService(db).GetStatusAsync(org.Id)).OrdersThisMonth.Should().Be(2);
    }

    // ── the paid SOFT cap is untouched (this is where money lives) ─────────

    [Fact]
    public async Task PaidPlan_FailedOrders_StillCountAgainstTheMonthlyQuota()
    {
        // The published pricing/Terms copy describes the paid meter as counting every order at
        // creation. Changing that is a copy change, not a bug fix — so it is NOT made here.
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 3, OrderStatusConstants.Delivered,          monthStart);
        SeedOrders(db, org.Id, sup, 2, OrderStatusConstants.Failed,             monthStart.AddMinutes(1));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.TransformFailed,    monthStart.AddMinutes(2));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryFailed,     monthStart.AddMinutes(3));
        SeedOrders(db, org.Id, sup, 1, OrderStatusConstants.DeliveryDeadLetter, monthStart.AddMinutes(4));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(8,
            "the paid meter is byte-identical to before — the Pilot forgiveness must not leak into it");
    }

    [Fact]
    public async Task PaidPlan_InvoicedOverageWindow_IsUnchangedByTheForgiveness()
    {
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 150, OrderStatusConstants.Delivered, monthStart);
        SeedOrders(db, org.Id, sup, 4,   OrderStatusConstants.Failed,    monthStart.AddHours(1));
        await db.SaveChangesAsync();

        var overage = await MakeService(db).ComputePeriodOverageOrdersAsync(
            org.Id, monthStart, DateTime.UtcNow.AddDays(1));

        overage.Should().Be(4,
            "the invoiced overage window still counts all 154 orders at creation ⇒ 4 over the 150 cap");
    }

    [Fact]
    public async Task PaidPlan_SoftCapStaysSoft_OverTheLimitWithFailuresStillProcesses()
    {
        // The regression to fear is a soft cap turning hard. Pin it: far over the cap, with
        // failures in the mix, a paid org in good standing still processes orders.
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        var monthStart = ThisMonthStart();
        SeedOrders(db, org.Id, sup, 400, OrderStatusConstants.Delivered, monthStart);
        SeedOrders(db, org.Id, sup, 25,  OrderStatusConstants.Failed,    monthStart.AddHours(1));
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(425);
        status.IsOrderLimitReached.Should().BeTrue();
        status.AtLimit.Should().BeTrue();
        status.CanProcessOrders.Should().BeTrue(
            "a paid plan's cap is SOFT — going over accrues overage and must NEVER block processing");
        status.OverageOrders.Should().BeGreaterThan(0, "the soft cap bills instead of blocking");
    }

    [Fact]
    public async Task PaidPlan_OverTheLimitWithOnlyFailures_StillProcesses()
    {
        // Same invariant from the other direction: a paid org whose volume is ENTIRELY failures
        // is over its cap and must still process — the forgiveness cannot be smuggled in here as
        // a "kindness" that would silently change what Stripe is invoiced.
        var db = MakeDb();
        var org = Org(PlanConstants.Growth, AccountStatusConstants.Active); // cap 150
        db.Organisations.Add(org);
        var sup = SeedSupplier(db, org.Id);
        SeedOrders(db, org.Id, sup, 160, OrderStatusConstants.Failed, ThisMonthStart());
        await db.SaveChangesAsync();

        var status = await MakeService(db).GetStatusAsync(org.Id);

        status.OrdersThisMonth.Should().Be(160, "paid metering is unchanged");
        status.CanProcessOrders.Should().BeTrue("volume never blocks a paid plan in good standing");
    }
}
