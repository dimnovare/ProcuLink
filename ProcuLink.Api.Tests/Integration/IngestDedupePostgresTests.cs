using ProcuLink.TestSupport;
using System.Data.Common;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// ONE postgres:16 container shared by every test in <see cref="IngestDedupePostgresTests"/>.
/// xUnit builds a fresh test-class instance per test method, so <c>IAsyncLifetime</c> on the CLASS
/// would start and migrate a container PER TEST; an <see cref="IClassFixture{T}"/> is constructed
/// once for the whole class instead. Each test isolates itself by using a brand-new org id rather
/// than a brand-new database.
/// </summary>
public sealed class IngestDedupeFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;

    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_ingest_dedupe");

        // These tests open many short-lived contexts on purpose — every seed, every assertion read,
        // and the racing "winner" all take their own. Pooling stays ON so those reuse connections
        // instead of opening a fresh TCP connection through Docker's port-forward each time; with
        // pooling off, a cold engine fails them at NpgsqlConnector.RawOpen (a CONNECTION timeout,
        // nothing to do with dedupe). The nested interceptor call is safe under pooling: the loser's
        // connection is busy, so the winner is handed a different one from the pool.
        ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = true,
            // Default is 15 s, which a freshly-restarted Docker Desktop can exceed on first connect.
            Timeout = 60,
        }.ConnectionString;

        Options = NpgsqlOptions(ConnectionString).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
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
        // Inbound mail now resolves its tenant through an issued address, not the slug column, so
        // an org that is supposed to receive mail has to be given one.
        await InboundAddressTestHarness.SeedAddressAsync(db, orgId, slug);
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

    private static readonly byte[] CsvBytes = Encoding.UTF8.GetBytes("po,qty\r\nWP22-1,7\r\n");

    // ── Gap 3: browser upload idempotency ───────────────────────────────────────────────────────

    private const string UploadKey = "upload-selection-b5-001";

    private static readonly byte[] UploadCsvBytes =
        Encoding.UTF8.GetBytes("po,qty\r\nB5-UPLOAD-1,3\r\n");

    private static IFormFile UploadFile(string fileName = "po.csv") =>
        new FormFile(new MemoryStream(UploadCsvBytes.ToArray()), 0, UploadCsvBytes.Length, "file", fileName)
        {
            Headers     = new HeaderDictionary(),
            ContentType = "text/csv",
        };

    /// <summary>
    /// An <see cref="OrdersController"/> wired for the upload path only: the REAL
    /// <see cref="IdempotencyService"/> over the container (so the claim row and its primary key are
    /// the actual guard), permissive billing, and the same double behind both order seams.
    /// </summary>
    private OrdersController BuildUpload(
        ProcuLinkDbContext db, Guid orgId, IOrderService orders, string? idempotencyKey = UploadKey,
        bool billingAllows = true)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(
                   Allowed: billingAllows, PilotExpired: false, Plan: "operations", Limit: 1000));

        var http = new DefaultHttpContext();
        if (idempotencyKey is not null)
            http.Request.Headers["Idempotency-Key"] = idempotencyKey;

        return new OrdersController(
            orders,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            billing.Object,
            new IdempotencyService(db),
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    /// <summary>Digs the order id out of the upload response's <c>order</c> DTO.</summary>
    private static Guid UploadedOrderIdOf(OkObjectResult ok)
    {
        var order = ok.Value!.GetType().GetProperty("order")?.GetValue(ok.Value);
        order.Should().NotBeNull("the upload response must carry the order");
        return ((ProcuLink.Api.Contracts.OrderDto)order!).Id;
    }

    private static bool IsReplay(OkObjectResult ok) =>
        ok.Value!.GetType().GetProperty("idempotentReplay")?.GetValue(ok.Value) as bool? == true;

    /// <summary>
    /// B-5, the P0 the whole finding is about: two uploads of the same file selection land at once —
    /// the double-click, or the retry after the 60-second client timeout. They must create ONE
    /// order, and the loser must be handed the winner's, not an error and not a second order.
    ///
    /// <para>The interleave is forced at the loser's read of <c>idempotency_keys</c>, which is
    /// exactly the window the old check-then-create pair left open: under
    /// <c>TryGetExistingOrderIdAsync</c> + a much-later <c>BindAsync</c>, both requests saw "no row"
    /// and both created an order.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task ConcurrentIdenticalUploadKeys_CreateOneOrder_AndLoserGetsWinnersOrder()
    {
        var (orgId, _)  = await SeedOrgAsync();
        var supplierId  = await SeedSupplierAsync(orgId);
        var orders      = new PersistingOrderService(Options);

        IActionResult? winnerResult = null;
        var interceptor = new FireOnSelectInterceptor("idempotency_keys", async () =>
        {
            await using var db = NewContext();
            winnerResult = await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);
        });

        var racingOptions = IngestDedupeFixture.NpgsqlOptions(_fx.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        IActionResult loserResult;
        await using (var db = new ProcuLinkDbContext(racingOptions))
        {
            loserResult = await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);
        }

        interceptor.Fired.Should().BeTrue("the uploads must actually have interleaved, or this proves nothing");

        (await OrderCountAsync(orgId)).Should().Be(1,
            "two concurrent uploads with the same Idempotency-Key must create EXACTLY ONE order");

        var winnerId = UploadedOrderIdOf(winnerResult.Should().BeOfType<OkObjectResult>().Subject);
        var loserOk  = loserResult.Should().BeOfType<OkObjectResult>(
            "the loser of an upload race is a successful replay, not an error").Subject;
        UploadedOrderIdOf(loserOk).Should().Be(winnerId, "the loser must receive the winner's order id");

        await using var verify = NewContext();
        (await verify.IdempotencyKeys.CountAsync(k => k.OrgId == orgId)).Should().Be(1,
            "exactly one claim row survives the race");
    }

    /// <summary>
    /// The sequential half of B-5: the user clicks upload, the response is lost, they click again.
    /// One order, and the second response says so.
    /// </summary>
    [DockerRequiredFact]
    public async Task SameUploadKeyTwice_CreatesOneOrder_AndSecondIsFlaggedAsReplay()
    {
        var (orgId, _) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var orders     = new PersistingOrderService(Options);

        OkObjectResult first, second;
        await using (var db = NewContext())
            first = (await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>().Subject;

        await using (var db = NewContext())
            second = (await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>().Subject;

        (await OrderCountAsync(orgId)).Should().Be(1,
            "a repeat upload of the same file selection must not create a second order");
        UploadedOrderIdOf(second).Should().Be(UploadedOrderIdOf(first));
        IsReplay(second).Should().BeTrue("the second response must identify itself as a replay");
        IsReplay(first).Should().BeFalse("the first response created the order; it is not a replay");
    }

    /// <summary>
    /// A crash between the order's creation and the key's binding. Under check-then-create the bind
    /// came LAST, so the retry saw an unbound key and created a SECOND order — the same duplicate
    /// the header was supposed to prevent. Claim-first has no such window.
    /// </summary>
    [DockerRequiredFact]
    public async Task UploadCrashAfterCreateBeforeBind_Retry_CreatesNoSecondOrder()
    {
        var (orgId, _) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);

        var attempt = 0;
        var orders = new PersistingOrderService(Options,
            () => Interlocked.Increment(ref attempt) == 1 ? CreateBehavior.SucceedThenThrow : CreateBehavior.Succeed);

        await using (var db = NewContext())
        {
            var controller = BuildUpload(db, orgId, orders);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.Upload(UploadFile(), supplierId, orders, CancellationToken.None));
        }

        (await OrderCountAsync(orgId)).Should().Be(1, "the first attempt did commit its order");

        await using (var db = NewContext())
        {
            var retry = await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);
            retry.Should().BeOfType<OkObjectResult>("the retry must succeed, never a permanently-blocked key");
        }

        (await OrderCountAsync(orgId)).Should().Be(1,
            "the retry must NOT create a second order for the same Idempotency-Key");
    }

    /// <summary>
    /// A crash between claim and create must never permanently block the key — the failure mode that
    /// makes "claim first" unsafe if creation is not a find-or-create on the claimed id.
    /// </summary>
    [DockerRequiredFact]
    public async Task UploadCrashAfterClaimBeforeCreate_Retry_StillCreatesTheOrder()
    {
        var (orgId, _) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);

        var attempt = 0;
        var orders = new PersistingOrderService(Options,
            () => Interlocked.Increment(ref attempt) == 1 ? CreateBehavior.ThrowTransient : CreateBehavior.Succeed);

        await using (var db = NewContext())
        {
            var controller = BuildUpload(db, orgId, orders);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.Upload(UploadFile(), supplierId, orders, CancellationToken.None));
        }

        (await OrderCountAsync(orgId)).Should().Be(0, "the transient failure created no order");

        Guid resumedId;
        await using (var db = NewContext())
        {
            var ok = (await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>(
                    "a key whose order was never created must remain resumable, never permanently blocked").Subject;
            resumedId = UploadedOrderIdOf(ok);
            resumedId.Should().NotBe(Guid.Empty, "the retry must return a real order id");
        }

        (await OrderCountAsync(orgId)).Should().Be(1, "the retry creates the order exactly once");

        // The order must live under the CLAIMED id, not a fresh one. Creating under a fresh id would
        // leave the claim pointing at an order that will never exist, so every later retry resumes
        // again and mints another order — the duplicate, arriving one attempt later.
        await using (var verify = NewContext())
        {
            var claim = await verify.IdempotencyKeys.SingleAsync(k => k.OrgId == orgId);
            claim.OrderId.Should().Be(resumedId, "the order must be created under the claimed id");
        }

        await using (var db = NewContext())
        {
            var third = await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);
            third.Should().BeOfType<OkObjectResult>();
        }

        (await OrderCountAsync(orgId)).Should().Be(1, "a further retry is a replay, not a third order");
    }

    /// <summary>
    /// The idempotency short-circuit must run BEFORE the billing gate — the same ordering
    /// <c>IngressController</c> states and for the same reason: an upload that was already accepted
    /// must keep returning its order when the client retries, not start 429-ing because the account
    /// crossed a limit in between. Gate-first would answer a duplicate-suppression request with
    /// "upgrade", and the client would reasonably retry again.
    /// </summary>
    [DockerRequiredFact]
    public async Task UploadReplay_AfterTheAccountStopsAllowingOrders_StillReturnsTheOriginalOrder()
    {
        var (orgId, _) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var orders     = new PersistingOrderService(Options);

        Guid originalId;
        await using (var db = NewContext())
        {
            var ok = (await BuildUpload(db, orgId, orders)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>().Subject;
            originalId = UploadedOrderIdOf(ok);
        }

        await using (var db = NewContext())
        {
            var replay = await BuildUpload(db, orgId, orders, billingAllows: false)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);

            var ok = replay.Should().BeOfType<OkObjectResult>(
                "a replay of an already-accepted upload must not be refused with 429").Subject;
            UploadedOrderIdOf(ok).Should().Be(originalId);
        }

        (await OrderCountAsync(orgId)).Should().Be(1);
    }

    /// <summary>
    /// The negative control, and the reason the frontend half of B-5 exists: with NO
    /// <c>Idempotency-Key</c> header there is nothing to claim, so two uploads are two orders. Any
    /// change that makes this pass by accident (deriving a key from the file bytes, say) would also
    /// swallow a deliberate re-send of a corrected PO.
    /// </summary>
    [DockerRequiredFact]
    public async Task TwoUploadsWithNoIdempotencyKey_StillCreateTwoOrders()
    {
        var (orgId, _) = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId);
        var orders     = new PersistingOrderService(Options);

        for (var i = 0; i < 2; i++)
        {
            await using var db = NewContext();
            var result = await BuildUpload(db, orgId, orders, idempotencyKey: null)
                .Upload(UploadFile(), supplierId, orders, CancellationToken.None);
            result.Should().BeOfType<OkObjectResult>();
        }

        (await OrderCountAsync(orgId)).Should().Be(2,
            "without a key the server has nothing to deduplicate on — the client must send one");

        await using var verify = NewContext();
        (await verify.IdempotencyKeys.CountAsync(k => k.OrgId == orgId)).Should().Be(0,
            "no key was supplied, so no claim row may be invented");
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
    private sealed class PersistingOrderService : IOrderService, IStubOrderCreator
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

        // ── IStubOrderCreator: the browser upload creates under the idempotency claim's id ──

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream, string filename,
            string contentType, CancellationToken ct)
            => PersistAsync(organisationId, supplierId, orderId, ct);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType,
            CancellationToken ct)
            => PersistAsync(organisationId, supplierId: null, orderId, ct);

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
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount, int SampleCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount, int SampleCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
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
