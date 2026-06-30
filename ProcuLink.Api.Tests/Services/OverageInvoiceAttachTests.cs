using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Stripe;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Finding 8.2 — overage invoice items must ATTACH to the triggering DRAFT invoice
/// instead of floating on the customer (where they sweep into the NEXT invoice:
/// ~a year of float on a yearly subscription, stranded entirely on cancellation).
/// Verified against a capturing <see cref="IStripeClient"/> (no HTTP):
///  • the attach overload sets InvoiceItemCreateOptions.Invoice = the draft invoice id;
///  • the customer-sweep overload leaves Invoice null (historical behaviour, byte-identical);
///  • the Stripe Idempotency-Key stays the period billing key in BOTH paths;
///  • the (orgId, billingKey) ledger idempotency still blocks a second Stripe call.
/// </summary>
public class OverageInvoiceAttachTests
{
    // ── capturing fake Stripe transport ─────────────────────────────────────

    private sealed class CapturingStripeClient : IStripeClient
    {
        public List<(HttpMethod Method, string Path, BaseOptions Options, RequestOptions RequestOptions)> Requests { get; } = new();

        public string ApiBase         => "https://api.stripe.invalid";
        public string ApiKey          => "sk_test_fake";
        public string ClientId        => "ca_fake";
        public string ConnectBase     => "https://connect.stripe.invalid";
        public string FilesBase       => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(
            HttpMethod method,
            string path,
            BaseOptions options,
            RequestOptions requestOptions,
            CancellationToken cancellationToken = default)
            where T : IStripeEntity
        {
            Requests.Add((method, path, options, requestOptions));
            var entity = Activator.CreateInstance<T>();
            typeof(T).GetProperty("Id")?.SetValue(entity, "ii_fake_123");
            return Task.FromResult(entity);
        }

        public Task<System.IO.Stream> RequestStreamingAsync(
            HttpMethod method,
            string path,
            BaseOptions options,
            RequestOptions requestOptions,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StripeBillingService MakeService(ProcuLinkDbContext db, IStripeClient stripe) =>
        new(db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Configured Stripe → the service takes the real billing path,
                // but all transport goes through the capturing fake (no HTTP).
                ["Stripe:SecretKey"] = "sk_test_fake",
            }).Build(),
            NullLogger<StripeBillingService>.Instance,
            new FakeAnalyticsService(),
            stripe);

    private static async Task<Organisation> AddPaidOrgAsync(ProcuLinkDbContext db)
    {
        var id = Guid.NewGuid();
        var org = new Organisation
        {
            Id               = id,
            ClerkOrgId       = $"org_{id:N}",
            Name             = "Attach Org",
            Slug             = $"attach-{id:N}",
            Plan             = PlanConstants.Growth,
            AccountStatus    = AccountStatusConstants.Active,
            StripeCustomerId = "cus_attach_test",
            CreatedAt        = DateTime.UtcNow.AddDays(-30),
            TrialStartedAt   = DateTime.UtcNow.AddDays(-30),
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    // ── attach path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AttachOverload_SetsInvoiceOnTheCreatedItem_AndKeepsThePeriodIdempotencyKey()
    {
        var db = MakeDb();
        var org = await AddPaidOrgAsync(db);
        var stripe = new CapturingStripeClient();
        var svc = MakeService(db, stripe);
        var key = $"{org.Id}:2026-01-01T00:00:00.0000000Z";

        var result = await svc.BillOverageForInvoiceAsync(org.Id, key, overageOrders: 5, stripeInvoiceId: "in_draft_123");

        var req = stripe.Requests.Should().ContainSingle("exactly one invoice-item create call").Subject;
        var options = req.Options.Should().BeOfType<InvoiceItemCreateOptions>().Subject;
        options.Invoice.Should().Be("in_draft_123",
            "the overage item must be pinned to the DRAFT period-close invoice, not float to the next one");
        options.Customer.Should().Be("cus_attach_test");
        options.Amount.Should().Be(250, "5 orders × €0.50 = 250 cents");
        req.RequestOptions.IdempotencyKey.Should().Be(key, "idempotency keys are unchanged by the attach");

        result.StripeItemId.Should().Be("ii_fake_123");
        result.AlreadyBilled.Should().BeFalse();
        (await db.OverageBillingRecords.SingleAsync(r => r.OrgId == org.Id && r.BillingKey == key))
            .StripeInvoiceItemId.Should().Be("ii_fake_123", "the ledger row records the created item");
    }

    [Fact]
    public async Task AttachOverload_ReplaySameKey_NeverMakesASecondStripeCall()
    {
        var db = MakeDb();
        var org = await AddPaidOrgAsync(db);
        var stripe = new CapturingStripeClient();
        var svc = MakeService(db, stripe);
        var key = $"{org.Id}:2026-02-01T00:00:00.0000000Z";

        var first  = await svc.BillOverageForInvoiceAsync(org.Id, key, 5, "in_draft_456");
        var second = await svc.BillOverageForInvoiceAsync(org.Id, key, 5, "in_draft_456");

        first.AlreadyBilled.Should().BeFalse();
        second.AlreadyBilled.Should().BeTrue("the (orgId, billingKey) ledger blocks the replay");
        stripe.Requests.Should().ContainSingle("a replayed webhook must never create a second invoice item");
    }

    [Fact]
    public async Task AttachOverload_BlankInvoiceId_Throws()
    {
        var db = MakeDb();
        var org = await AddPaidOrgAsync(db);
        var svc = MakeService(db, new CapturingStripeClient());

        var act = () => svc.BillOverageForInvoiceAsync(org.Id, "key", 5, stripeInvoiceId: " ");

        await act.Should().ThrowAsync<ArgumentException>("the attach overload requires a real invoice id");
    }

    // ── customer-sweep path (historical behaviour, byte-identical) ──────────

    [Fact]
    public async Task SweepOverload_LeavesInvoiceUnset_OnTheCreatedItem()
    {
        var db = MakeDb();
        var org = await AddPaidOrgAsync(db);
        var stripe = new CapturingStripeClient();
        var svc = MakeService(db, stripe);
        var key = $"{org.Id}:2026-03-01T00:00:00.0000000Z";

        var result = await svc.BillOverageForInvoiceAsync(org.Id, key, overageOrders: 4);

        var req = stripe.Requests.Should().ContainSingle().Subject;
        var options = req.Options.Should().BeOfType<InvoiceItemCreateOptions>().Subject;
        options.Invoice.Should().BeNull(
            "the non-attach overload keeps the historical customer-sweep behaviour byte-identical");
        options.Customer.Should().Be("cus_attach_test");
        req.RequestOptions.IdempotencyKey.Should().Be(key);
        result.AmountCents.Should().Be(200);
    }

    // ── poison-ledger recovery: a Stripe failure AFTER the ledger row is ─────
    // committed must NOT permanently suppress the charge on a later retry ─────

    /// <summary>
    /// A Stripe transport that throws on its FIRST request (simulating a transient
    /// 5xx / rate-limit / network error at period close) and succeeds on every
    /// request after that.
    /// </summary>
    private sealed class FailThenSucceedStripeClient : IStripeClient
    {
        private int _calls;
        public int CallCount => _calls;

        public string ApiBase         => "https://api.stripe.invalid";
        public string ApiKey          => "sk_test_fake";
        public string ClientId        => "ca_fake";
        public string ConnectBase     => "https://connect.stripe.invalid";
        public string FilesBase       => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(
            HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default)
            where T : IStripeEntity
        {
            _calls++;
            if (_calls == 1)
                throw new StripeException("Simulated transient Stripe failure at period close.");
            var entity = Activator.CreateInstance<T>();
            typeof(T).GetProperty("Id")?.SetValue(entity, "ii_recovered_1");
            return Task.FromResult(entity);
        }

        public Task<System.IO.Stream> RequestStreamingAsync(
            HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not used by these tests.");
    }

    [Fact]
    public async Task StripeFailsAfterLedgerCommit_RetryRecovers_AndBillsExactlyOnce()
    {
        var db = MakeDb();
        var org = await AddPaidOrgAsync(db);
        var stripe = new FailThenSucceedStripeClient();
        var svc = MakeService(db, stripe);
        var key = $"{org.Id}:2026-04-01T00:00:00.0000000Z";

        // ── Attempt 1: ledger row commits, then the Stripe call throws ──────
        var attempt1 = async () => await svc.BillOverageForInvoiceAsync(org.Id, key, overageOrders: 10, stripeInvoiceId: "in_draft_poison");
        await attempt1.Should().ThrowAsync<StripeException>(
            "the Stripe call fails AFTER the ledger row is committed — the webhook would 500 and Stripe retries");

        var afterFail = await db.OverageBillingRecords.SingleAsync(r => r.OrgId == org.Id && r.BillingKey == key);
        afterFail.StripeInvoiceItemId.Should().BeNull(
            "the slot was claimed but no Stripe item exists yet — this is the poison-ledger state");
        afterFail.AmountCents.Should().Be(500);

        // ── Attempt 2 (Stripe healthy): the retry MUST re-attempt Stripe ─────
        // and NOT report AlreadyBilled — otherwise the €5.00 is lost forever.
        var attempt2 = await svc.BillOverageForInvoiceAsync(org.Id, key, overageOrders: 10, stripeInvoiceId: "in_draft_poison");
        attempt2.AlreadyBilled.Should().BeFalse(
            "a NULL-item ledger row must not suppress the retry while Stripe is the billing authority");
        attempt2.StripeItemId.Should().Be("ii_recovered_1", "the retry created the real Stripe item");

        stripe.CallCount.Should().Be(2, "exactly one failed attempt + one successful retry");
        (await db.OverageBillingRecords.SingleAsync(r => r.OrgId == org.Id && r.BillingKey == key))
            .StripeInvoiceItemId.Should().Be("ii_recovered_1", "the recovered item id is persisted onto the same single row");
        db.OverageBillingRecords.Count(r => r.OrgId == org.Id && r.BillingKey == key)
            .Should().Be(1, "still exactly one ledger row — no duplicate, no double-bill");

        // ── Attempt 3: a normal replay (row now HAS an item id) is a no-op ───
        var attempt3 = await svc.BillOverageForInvoiceAsync(org.Id, key, overageOrders: 10, stripeInvoiceId: "in_draft_poison");
        attempt3.AlreadyBilled.Should().BeTrue("once the item id is set, replays are clean idempotent no-ops");
        stripe.CallCount.Should().Be(2, "the no-op replay must not make a third Stripe call");
    }
}
