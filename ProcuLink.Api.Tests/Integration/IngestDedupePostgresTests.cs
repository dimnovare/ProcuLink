using System.Data.Common;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Email;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// ONE postgres:16 container shared by every test in <see cref="IngestDedupePostgresTests"/>.
/// xUnit builds a fresh test-class instance per test method, so <c>IAsyncLifetime</c> on the CLASS
/// would start and migrate a container PER TEST; an <see cref="IClassFixture{T}"/> is constructed
/// once for the whole class instead. Each test isolates itself by using a brand-new org id rather
/// than a brand-new database.
/// </summary>
public sealed class IngestDedupeFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;

    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_ingest_dedupe_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        // These tests open many short-lived contexts on purpose — every seed, every assertion read,
        // and the racing "winner" all take their own. Pooling stays ON so those reuse connections
        // instead of opening a fresh TCP connection through Docker's port-forward each time; with
        // pooling off, a cold engine fails them at NpgsqlConnector.RawOpen (a CONNECTION timeout,
        // nothing to do with dedupe). The nested interceptor call is safe under pooling: the loser's
        // connection is busy, so the winner is handed a different one from the pool.
        ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = true,
            // Default is 15 s, which a freshly-restarted Docker Desktop can exceed on first connect.
            Timeout = 60,
        }.ConnectionString;

        Options = NpgsqlOptions(ConnectionString).Options;

        await using var migrateDb = new ProcuLinkDbContext(Options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    /// <summary>
    /// Npgsql options for these tests, with a command timeout well above the default 30 s.
    /// <para>The racing tests interleave by running the WINNER's entire request from inside a
    /// <c>DbCommandInterceptor</c> on the LOSER's context — so the loser's statement is still open
    /// across a full nested round-trip (claim insert, order insert, audit write). On a cold or
    /// loaded Docker engine that nested work alone can exceed 30 s, and the loser's read then dies
    /// with Npgsql's "Timeout during reading attempt" — the same environmental flakiness
    /// <see cref="PostgresContainerCollection"/> was created to damp. Raising the timeout keeps the
    /// interleave and every assertion identical; it only stops a slow engine from being reported as
    /// a dedupe defect.</para>
    /// </summary>
    public static DbContextOptionsBuilder<ProcuLinkDbContext> NpgsqlOptions(string connectionString) =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(180));
}

/// <summary>
/// WP-22 — duplicate prevention on the two PUSH ingress channels, proved on REAL Postgres where a
/// unique index is actually enforced (EF InMemory silently ignores one, so it can never prove a
/// claim race).
///
/// <para><b>Gap 1 — Postmark inbound email had no dedupe at all.</b> The IMAP PULL path claims each
/// attachment in <c>email_import_records</c> before creating the order; the Postmark PUSH path went
/// straight to <c>CreateStub…</c>. Postmark retries any non-200 ten times over ~10.5 hours, so one
/// transient failure AFTER the order was created produced a duplicate order — and a duplicate
/// supplier delivery — per retry.</para>
///
/// <para><b>Gap 2 — REST ingress idempotency was check-then-create.</b>
/// <c>TryGetExistingOrderIdAsync</c> then (much later) <c>BindAsync</c>: two concurrent requests
/// with the same <c>Idempotency-Key</c> both saw "no row" and both created an order, and a crash
/// between create and bind duplicated on the next retry.</para>
///
/// <para><b>Determinism.</b> No <c>Task.WhenAll</c> guesswork: a <see cref="DbCommandInterceptor"/>
/// on the LOSER's own <c>DbContext</c> runs the winner's whole request at the exact statement the
/// window opens at, so the interleave is identical every run. Each racing test asserts the
/// interceptor actually fired — without that, a test that never interleaved would "pass" while
/// proving nothing.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class IngestDedupePostgresTests : IClassFixture<IngestDedupeFixture>
{
    private readonly IngestDedupeFixture _fx;

    public IngestDedupePostgresTests(IngestDedupeFixture fx) => _fx = fx;

    private DbContextOptions<ProcuLinkDbContext> Options => _fx.Options!;
    private ProcuLinkDbContext NewContext() => new(Options);

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, string Slug)> SeedOrgAsync()
    {
        var orgId = Guid.NewGuid();
        var slug = $"wp22-{orgId:N}";
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_wp22_{orgId:N}",
            Name = "WP22 Dedupe Org",
            Slug = slug,
            Plan = "operations",
            AccountStatus = AccountStatusConstants.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, slug);
    }

    private async Task<Guid> SeedSupplierAsync(Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        await using var db = NewContext();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Northwind Trading", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private async Task<int> OrderCountAsync(Guid orgId)
    {
        await using var db = NewContext();
        return await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
    }

    // ── Postmark payload helpers ────────────────────────────────────────────────────────────────

    private static readonly byte[] CsvBytes = Encoding.UTF8.GetBytes("po,qty\r\nWP22-1,7\r\n");

    /// <summary>
    /// One Postmark delivery of the same logical message: identical recipient, identical attachment
    /// bytes. A fresh <see cref="InboundEmailPayload"/> per call, because a re-delivery really is a
    /// separate decode of the same wire bytes.
    /// </summary>
    /// <summary>
    /// A realistic Postmark message id. Every cell supplies one because PRODUCTION always does
    /// (<c>InboundEmailController</c> passes <c>body.MessageID</c>), and because a payload built
    /// without one is not a weaker test — it is a DIFFERENT test. With <c>ProviderMessageId</c>
    /// null, <c>ClaimKeyFor</c> returns the bare <c>"postmark:"</c> prefix, so the whole suite
    /// silently exercises content-hash-only dedupe in a single org-wide bucket and proves nothing
    /// about the message id at all.
    /// </summary>
    private const string DefaultPostmarkMessageId = "a1b2c3d4-1111-2222-3333-444455556666";

    private static InboundEmailPayload PostmarkPayload(
        string slug, string fileName = "po.csv", string? messageId = DefaultPostmarkMessageId) =>
        new(FromEmail: "buyer@heinrich.example.com",
            ToEmail: $"orders@{slug}.proculink.eu",
            Subject: "PO attached",
            Attachments: new[] { new InboundAttachment(fileName, "text/csv", CsvBytes.ToArray()) },
            ProviderMessageId: messageId);

    private static InboundEmailPayload PostmarkBodyOnlyPayload(
        string slug, string? messageId = DefaultPostmarkMessageId) =>
        new(FromEmail: "buyer@heinrich.example.com",
            ToEmail: $"orders@{slug}.proculink.eu",
            Subject: "PO in the body",
            Attachments: Array.Empty<InboundAttachment>(),
            Body: "Please supply 7 x WP22-1 widgets at 4.50 EUR each. PO number WP22-BODY-1.",
            ProviderMessageId: messageId);

    private static InboundEmailRouter MakeRouter(
        ProcuLinkDbContext db,
        IClaimedOrderCreator orders,
        IParseJobEnqueuer enqueuer,
        IEmailBodyOrderExtractor? bodyExtractor = null)
        => new(db, orders, enqueuer,
            bodyExtractor ?? NoOrderBodyExtractor.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<InboundEmailRouter>.Instance);

    // ── Gap 1: Postmark replay ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC: the same Postmark message replayed twice creates ONE order. This is the plain
    /// retry — Postmark re-POSTs the identical envelope after a non-200 (or after a 200 it never
    /// saw), ten times over ~10.5 hours.
    /// </summary>
    [DockerRequiredFact]
    public async Task SamePostmarkMessage_ReplayedTwice_CreatesOneOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();

        await using (var db1 = NewContext())
            (await MakeRouter(db1, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None))
                .Success.Should().BeTrue();

        await using (var db2 = NewContext())
            (await MakeRouter(db2, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None))
                .Success.Should().BeTrue("a replayed message is still handled — it just must not re-import");

        (await OrderCountAsync(orgId)).Should().Be(1,
            "the same inbound email replayed by Postmark must create EXACTLY ONE order");
        orders.TotalCreates.Should().Be(1,
            "the replay must not reach order creation a second time");
    }

    /// <summary>
    /// THE cell that makes the message id load-bearing. Two genuinely different emails carrying
    /// BYTE-IDENTICAL attachments — a recurring weekly PO on a standard template, the same order
    /// form re-sent for a second site — must produce TWO orders.
    ///
    /// <para>Without <c>ProviderMessageId</c> reaching the router, <c>ClaimKeyFor</c> returns the
    /// bare <c>"postmark:"</c> prefix and the ledger key degenerates to
    /// <c>(orgId, "postmark:", sha256(attachment))</c> — ONE bucket per org. The second email is
    /// then skipped, and the webhook answers 200 with <c>Success: true</c> and an empty
    /// <c>CreatedOrderIds</c>: a silently LOST purchase order, with no alarm and no audit row.
    /// <c>IngressDedupe</c>'s own contract calls that outcome worse than the duplicate the claim
    /// exists to prevent.</para>
    ///
    /// <para>This is the mutation the rest of the suite could not see: deleting
    /// <c>ProviderMessageId: body.MessageID</c> from <c>InboundEmailController</c> is a legal
    /// compile (the parameter is optional) and left every other cell green.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task TwoDifferentMessages_WithIdenticalAttachments_CreateTwoOrders()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();

        await using (var db1 = NewContext())
            (await MakeRouter(db1, orders, enqueuer)
                .RouteAsync(PostmarkPayload(slug, messageId: "msg-first-0001"), CancellationToken.None))
                .Success.Should().BeTrue();

        await using (var db2 = NewContext())
            (await MakeRouter(db2, orders, enqueuer)
                .RouteAsync(PostmarkPayload(slug, messageId: "msg-second-0002"), CancellationToken.None))
                .Success.Should().BeTrue();

        (await OrderCountAsync(orgId)).Should().Be(2,
            "two DIFFERENT emails must each produce an order even when their attachments are " +
            "byte-identical — the provider message id is the dedupe key, not the attachment hash");
        orders.TotalCreates.Should().Be(2);
    }

    /// <summary>
    /// The replay half of the same contract, stated over the message id rather than the bytes: the
    /// SAME message id replayed with the same attachment is one order. Together with the cell above
    /// this pins both directions — same id ⇒ dedupe, different id ⇒ do not.
    /// </summary>
    [DockerRequiredFact]
    public async Task SameMessageId_ReplayedWithTheSameAttachment_CreatesOneOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();

        for (var attempt = 0; attempt < 3; attempt++)
            await using (var db = NewContext())
                (await MakeRouter(db, orders, enqueuer)
                    .RouteAsync(PostmarkPayload(slug, messageId: "msg-retried-0003"), CancellationToken.None))
                    .Success.Should().BeTrue();

        (await OrderCountAsync(orgId)).Should().Be(1,
            "Postmark re-POSTs a non-200 ten times over ~10.5 hours; every retry carries the same " +
            "MessageID and must land on the same claim");
        orders.TotalCreates.Should().Be(1);
    }

    /// <summary>
    /// The claim key must carry the <c>postmark:</c> namespace, so a Postmark push and an IMAP pull
    /// of the same RFC-822 Message-Id cannot collide in the shared <c>email_import_records</c>
    /// ledger. Asserted on the stored row rather than through behaviour, because behaviour cannot
    /// distinguish "namespaced" from "happens not to collide in this fixture".
    /// </summary>
    [DockerRequiredFact]
    public async Task ThePostmarkClaimKey_IsNamespaced_SoItCannotAliasAnImapMessageId()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);

        await using (var db = NewContext())
            (await MakeRouter(db, orders, new CountingEnqueuer())
                .RouteAsync(PostmarkPayload(slug, messageId: "<abc@mail.example.com>"), CancellationToken.None))
                .Success.Should().BeTrue();

        await using var read = NewContext();
        var keys = await read.EmailImportRecords
            .Where(r => r.OrgId == orgId)
            .Select(r => r.ImapMessageId)
            .ToListAsync();

        keys.Should().ContainSingle();
        keys[0].Should().Be("postmark:<abc@mail.example.com>",
            "the raw provider id must be prefixed, or a Postmark push and an IMAP pull of the same " +
            "message would claim the same ledger row");
    }

    /// <summary>
    /// The body-NLP fallback is a second order-creating path in the same router, so it needs the
    /// same claim. Without one, a replayed body-only email produces a duplicate order per retry.
    /// </summary>
    [DockerRequiredFact]
    public async Task SamePostmarkBodyOnlyMessage_ReplayedTwice_CreatesOneOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();
        var extractor = new FixedOrderBodyExtractor();

        await using (var db1 = NewContext())
            (await MakeRouter(db1, orders, enqueuer, extractor)
                .RouteAsync(PostmarkBodyOnlyPayload(slug), CancellationToken.None))
                .CreatedOrderIds.Should().HaveCount(1, "the body extractor yields an order on the first delivery");

        await using (var db2 = NewContext())
            await MakeRouter(db2, orders, enqueuer, extractor)
                .RouteAsync(PostmarkBodyOnlyPayload(slug), CancellationToken.None);

        (await OrderCountAsync(orgId)).Should().Be(1,
            "a replayed body-only email must create EXACTLY ONE order");
    }

    /// <summary>
    /// AC: a crash between claim and create leaves a RESUMABLE state, never a lost order. The claim
    /// must already be committed (carrying a real pre-generated order id) while no order exists, and
    /// the next delivery must resume under that same id — the pull channels' contract.
    /// </summary>
    [DockerRequiredFact]
    public async Task PostmarkTransientFailureAfterClaim_IsResumed_NotLost()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var attempt = 0;
        var orders = new PersistingOrderService(Options,
            () => Interlocked.Increment(ref attempt) == 1 ? CreateBehavior.ThrowTransient : CreateBehavior.Succeed);
        var enqueuer = new CountingEnqueuer();

        await using (var db1 = NewContext())
        {
            var router = MakeRouter(db1, orders, enqueuer);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => router.RouteAsync(PostmarkPayload(slug), CancellationToken.None));
        }

        Guid claimedOrderId;
        await using (var afterFail = NewContext())
        {
            var claim = await afterFail.EmailImportRecords.SingleAsync(r => r.OrgId == orgId);
            claim.OrderId.Should().NotBe(Guid.Empty,
                "the claim must carry a real pre-generated order id to resume under");
            claimedOrderId = claim.OrderId;
            (await afterFail.PurchaseOrders.CountAsync(o => o.OrgId == orgId)).Should().Be(0,
                "the transient failure means no order was created");
        }

        await using (var db2 = NewContext())
            (await MakeRouter(db2, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None))
                .Success.Should().BeTrue();

        await using var verify = NewContext();
        var surviving = await verify.PurchaseOrders.Where(o => o.OrgId == orgId).ToListAsync();
        surviving.Should().HaveCount(1, "the order is eventually created exactly once — never lost");
        surviving[0].Id.Should().Be(claimedOrderId,
            "the resume must recreate the order under the claim's pre-generated id, not a fresh one");
    }

    /// <summary>
    /// Two Postmark deliveries of the same message in flight at once. The interleave is forced at
    /// the LOSER's organisation read — a statement both the old and new code paths execute — so the
    /// winner completes its whole route before the loser does any dedupe work.
    /// </summary>
    [DockerRequiredFact]
    public async Task ConcurrentPostmarkDeliveries_OfSameMessage_CreateExactlyOneOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();

        var interceptor = new FireOnSelectInterceptor("organisations", async () =>
        {
            await using var db = NewContext();
            await MakeRouter(db, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None);
        });

        var racingOptions = IngestDedupeFixture.NpgsqlOptions(_fx.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using (var loser = new ProcuLinkDbContext(racingOptions))
            await MakeRouter(loser, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None);

        interceptor.Fired.Should().BeTrue("the deliveries must actually have interleaved, or this proves nothing");
        (await OrderCountAsync(orgId)).Should().Be(1,
            "two concurrent deliveries of one message must create EXACTLY ONE order");
    }

    /// <summary>
    /// The harder interleave: the loser has ALREADY passed its claim-existence fast path when the
    /// winner commits its claim. Only the unique index can stop the duplicate here — which is the
    /// whole point of claiming first rather than checking first.
    /// </summary>
    [DockerRequiredFact]
    public async Task ConcurrentPostmarkClaims_RacingOnTheUniqueIndex_CreateExactlyOneOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var orders = new PersistingOrderService(Options);
        var enqueuer = new CountingEnqueuer();

        var interceptor = new FireOnSelectInterceptor("email_import_records", async () =>
        {
            await using var db = NewContext();
            await MakeRouter(db, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None);
        });

        var racingOptions = IngestDedupeFixture.NpgsqlOptions(_fx.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using (var loser = new ProcuLinkDbContext(racingOptions))
            await MakeRouter(loser, orders, enqueuer).RouteAsync(PostmarkPayload(slug), CancellationToken.None);

        interceptor.Fired.Should().BeTrue(
            "the loser must have read the claim ledger and found nothing before the winner claimed — " +
            "if this never fired there is no claim-existence check to race, and the unique index is untested");
        (await OrderCountAsync(orgId)).Should().Be(1,
            "the unique index must be the real guard when both deliveries pass the existence check");

        await using var verify = NewContext();
        (await verify.EmailImportRecords.CountAsync(r => r.OrgId == orgId)).Should().Be(1,
            "exactly one ledger row survives the race");
    }

    // ── Gap 2: REST ingress idempotency ─────────────────────────────────────────────────────────

    private const string IdempotencyKey = "zapier-task-wp22-001";

    private (IngressController Controller, DefaultHttpContext Http) BuildIngress(
        ProcuLinkDbContext db, Guid orgId, string? idempotencyKey = IdempotencyKey)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim("org_id", orgId.ToString()) }, "ApiKey")),
        };
        http.Items[CurrentTenantService.Items.OrganisationId] = orgId;
        if (idempotencyKey is not null)
            http.Request.Headers["Idempotency-Key"] = idempotencyKey;

        var controller = new IngressController(
            db,
            new IdempotencyService(db),
            new CurrentTenantService(new HttpContextAccessor { HttpContext = http }),
            // Permissive billing: these cells are about the dedupe claim, not the plan gate.
            // The gate itself is pinned by IngestPathBillingGateTests.
            TestDoubles.PermissiveBilling.Service(),
            NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return (controller, http);
    }

    private static IngressOrderRequest IngressRequest(Guid supplierId) => new(
        OrderNumber: "PO-WP22-001",
        OrderDate: new DateOnly(2026, 7, 30),
        Currency: "EUR",
        Notes: null,
        SupplierId: supplierId.ToString(),
        Lines: new List<IngressOrderLine>
        {
            new(BuyerItemCode: "BUY-1", Description: "Widget", Quantity: 10m, Unit: "EA", UnitPrice: 4.50m),
        });

    /// <summary>
    /// AC: two concurrent requests with the same <c>Idempotency-Key</c> create ONE order, and the
    /// loser gets the winner's result rather than an error. The interleave is forced at the loser's
    /// idempotency-key read — the exact check-then-create window.
    /// </summary>
    [DockerRequiredFact]
    public async Task ConcurrentIdenticalIdempotencyKeys_CreateOneOrder_AndLoserGetsWinnersOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var orders = new PersistingOrderService(Options);
        var req = IngressRequest(supplierId);

        IActionResult? winnerResult = null;
        var interceptor = new FireOnSelectInterceptor("idempotency_keys", async () =>
        {
            await using var db = NewContext();
            var (winner, _) = BuildIngress(db, orgId);
            winnerResult = await winner.ReceiveOrder(slug, req, orders, orders, CancellationToken.None);
        });

        var racingOptions = IngestDedupeFixture.NpgsqlOptions(_fx.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        IActionResult loserResult;
        await using (var db = new ProcuLinkDbContext(racingOptions))
        {
            var (loser, _) = BuildIngress(db, orgId);
            loserResult = await loser.ReceiveOrder(slug, req, orders, orders, CancellationToken.None);
        }

        interceptor.Fired.Should().BeTrue("the requests must actually have interleaved, or this proves nothing");

        (await OrderCountAsync(orgId)).Should().Be(1,
            "two concurrent requests with the same Idempotency-Key must create EXACTLY ONE order");

        // The loser must be handed the winner's order, not a 4xx/5xx and not a second order.
        var winnerId = OrderIdOf(winnerResult.Should().BeOfType<OkObjectResult>().Subject);
        var loserOk = loserResult.Should().BeOfType<OkObjectResult>(
            "the loser of an idempotency race is a successful replay, not an error").Subject;
        OrderIdOf(loserOk).Should().Be(winnerId, "the loser must receive the winner's order id");
    }

    /// <summary>
    /// A crash between the order's creation and the key's binding. Under check-then-create the bind
    /// came last, so the retry saw an unbound key and created a SECOND order — the same duplicate
    /// the header was supposed to prevent.
    /// </summary>
    [DockerRequiredFact]
    public async Task IngressCrashAfterCreateBeforeBind_Retry_CreatesNoSecondOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var req = IngressRequest(supplierId);

        // First attempt: the order commits, then the process dies before the key is durable.
        var attempt = 0;
        var orders = new PersistingOrderService(Options,
            () => Interlocked.Increment(ref attempt) == 1 ? CreateBehavior.SucceedThenThrow : CreateBehavior.Succeed);

        await using (var db = NewContext())
        {
            var (controller, _) = BuildIngress(db, orgId);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.ReceiveOrder(slug, req, orders, orders, CancellationToken.None));
        }

        (await OrderCountAsync(orgId)).Should().Be(1, "the first attempt did commit its order");

        await using (var db = NewContext())
        {
            var (controller, _) = BuildIngress(db, orgId);
            var retry = await controller.ReceiveOrder(slug, req, orders, orders, CancellationToken.None);
            retry.Should().BeOfType<OkObjectResult>("the retry must succeed, never a permanently-blocked key");
        }

        (await OrderCountAsync(orgId)).Should().Be(1,
            "the at-least-once retry must NOT create a second order for the same Idempotency-Key");
    }

    /// <summary>
    /// AC: a crash between claim and create must never permanently block the key. The first attempt
    /// dies before any order exists; the retry must still produce one.
    /// </summary>
    [DockerRequiredFact]
    public async Task IngressCrashAfterClaimBeforeCreate_Retry_StillCreatesTheOrder()
    {
        var (orgId, slug) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var req = IngressRequest(supplierId);

        var attempt = 0;
        var orders = new PersistingOrderService(Options,
            () => Interlocked.Increment(ref attempt) == 1 ? CreateBehavior.ThrowTransient : CreateBehavior.Succeed);

        await using (var db = NewContext())
        {
            var (controller, _) = BuildIngress(db, orgId);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.ReceiveOrder(slug, req, orders, orders, CancellationToken.None));
        }

        (await OrderCountAsync(orgId)).Should().Be(0, "the transient failure created no order");

        await using (var db = NewContext())
        {
            var (controller, _) = BuildIngress(db, orgId);
            var retry = await controller.ReceiveOrder(slug, req, orders, orders, CancellationToken.None);
            var ok = retry.Should().BeOfType<OkObjectResult>(
                "a key whose order was never created must remain resumable, never permanently blocked").Subject;
            OrderIdOf(ok).Should().NotBe(Guid.Empty, "the retry must return a real order id");
        }

        (await OrderCountAsync(orgId)).Should().Be(1, "the retry creates the order exactly once");
    }

    private static Guid OrderIdOf(OkObjectResult ok)
    {
        var value = ok.Value!;
        var prop = value.GetType().GetProperty("Id") ?? value.GetType().GetProperty("id");
        prop.Should().NotBeNull("the ingress response must carry the order id");
        return (Guid)prop!.GetValue(value)!;
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>How the <see cref="PersistingOrderService"/> behaves on a given creation call.</summary>
    private enum CreateBehavior
    {
        Succeed,
        /// <summary>Throws BEFORE committing — an R2/Neon blip or a SIGTERM mid-create.</summary>
        ThrowTransient,
        /// <summary>Commits the order, THEN throws — a crash after the order is durable.</summary>
        SucceedThenThrow,
    }

    /// <summary>
    /// <see cref="IOrderService"/> double that SELF-COMMITS real order rows to the container, so
    /// every assertion is against committed state. Creation is find-or-create on the caller-supplied
    /// id when one is given (mirroring <c>OrderService</c>'s pre-generated-id contract), so a resume
    /// under the same id can never produce a second order. Only the entry points these ingress paths
    /// use are implemented; the rest throw, so an unexpected call fails loudly.
    /// </summary>
    private sealed class PersistingOrderService : IOrderService, IClaimedOrderCreator
    {
        private readonly DbContextOptions<ProcuLinkDbContext> _options;
        private readonly Func<CreateBehavior>? _behavior;
        private int _creates;

        public PersistingOrderService(
            DbContextOptions<ProcuLinkDbContext> options, Func<CreateBehavior>? behavior = null)
        {
            _options = options;
            _behavior = behavior;
        }

        public int TotalCreates => Volatile.Read(ref _creates);

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType,
            CancellationToken ct, string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId, orderId: null, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType,
            CancellationToken ct, string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId: null, orderId: null, ct);

        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
            Guid organisationId, Guid supplierId, ExtractedOrder order, string source,
            CancellationToken ct, string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId, orderId: null, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubFromParsedOrderAsync(
            Guid organisationId, ExtractedOrder order, string source,
            CancellationToken ct, string? inboundSenderDomain = null)
            => PersistAsync(organisationId, supplierId: null, orderId: null, ct);

        // ── IClaimedOrderCreator: creation under the ingress's pre-generated id ──

        public Task<Result<PurchaseOrderEntity>> CreateClaimedStubAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, Stream fileStream, string filename,
            string contentType, string? inboundSenderDomain, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, orderId, ct);

        public Task<Result<PurchaseOrderEntity>> CreateClaimedFromParsedOrderAsync(
            Guid organisationId, Guid? supplierId, Guid orderId, ExtractedOrder order, string source,
            string? inboundSenderDomain, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, orderId, ct);

        private async Task<Result<PurchaseOrderEntity>> PersistAsync(
            Guid orgId, Guid? supplierId, Guid? orderId, CancellationToken ct)
        {
            Interlocked.Increment(ref _creates);

            var behavior = _behavior?.Invoke() ?? CreateBehavior.Succeed;
            if (behavior == CreateBehavior.ThrowTransient)
                throw new InvalidOperationException("transient create failure (test double)");

            var id = orderId is { } provided && provided != Guid.Empty ? provided : Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using var db = new ProcuLinkDbContext(_options);
            var existing = await db.PurchaseOrders
                .FirstOrDefaultAsync(o => o.Id == id && o.OrgId == orgId, ct);
            if (existing is null)
            {
                existing = new PurchaseOrderEntity
                {
                    Id = id, OrgId = orgId, SupplierId = supplierId,
                    PoNumber = "PO-WP22", Currency = "EUR", Status = OrderStatusConstants.Parsing,
                    OrderDate = DateOnly.FromDateTime(now),
                    CreatedAt = now, UpdatedAt = now,
                };
                db.PurchaseOrders.Add(existing);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* concurrent create of the same PK — already there */ }
            }

            if (behavior == CreateBehavior.SucceedThenThrow)
                throw new InvalidOperationException("crash after the order committed (test double)");

            return Result<PurchaseOrderEntity>.Ok(existing);
        }

        public async Task<Result<PurchaseOrderEntity>> GetByIdAsync(
            Guid organisationId, Guid orderId, CancellationToken ct)
        {
            await using var db = new ProcuLinkDbContext(_options);
            var found = await db.PurchaseOrders
                .AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.Id == orderId && o.OrgId == organisationId, ct);
            return found is null
                ? Result<PurchaseOrderEntity>.Fail("Order not found.")
                : Result<PurchaseOrderEntity>.Ok(found);
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid organisationId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid organisationId, Guid orderId, IReadOnlyList<LineResolution> resolutions, bool saveMappings, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class CountingEnqueuer : IParseJobEnqueuer
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOrderBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly NoOrderBodyExtractor Instance = new();
        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
            => Task.FromResult(new EmailBodyExtractionResult(false, 0, null, "no-op fake"));
    }

    /// <summary>Always extracts the same one-line order, so a replayed body is byte-identical work.</summary>
    private sealed class FixedOrderBodyExtractor : IEmailBodyOrderExtractor
    {
        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
            => Task.FromResult(new EmailBodyExtractionResult(
                Success: true, Confidence: 0.91,
                Order: new ExtractedOrder(
                    PoNumber: "WP22-BODY-1",
                    OrderDate: new DateTime(2026, 7, 30),
                    BuyerName: "Heinrich",
                    Currency: "EUR",
                    Lines: new List<ExtractedOrderLine>
                    {
                        new(LineNumber: 1, BuyerItemCode: "WP22-1", Description: "Widget",
                            Quantity: 7m, Unit: "EA", UnitPrice: 4.50m),
                    }),
                FailureReason: null));
    }

    /// <summary>
    /// Runs <c>race</c> exactly once, immediately after the first SELECT that touches
    /// <c>table</c> — putting the winner's whole request inside the loser's window, identically
    /// every run. <see cref="Fired"/> is asserted by every racing test: an interleave that never
    /// happened would otherwise look like a pass.
    /// </summary>
    private sealed class FireOnSelectInterceptor : DbCommandInterceptor
    {
        private readonly string _table;
        private readonly Func<Task> _race;
        private int _fired;

        public FireOnSelectInterceptor(string table, Func<Task> race)
        {
            _table = table;
            _race = race;
        }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            var text = command.CommandText.TrimStart();
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && text.Contains(_table, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await _race();
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
