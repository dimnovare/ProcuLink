using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.TestSupport;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// Finding A4 / A4b — webhook delivery reliability.
///
/// A4 : the webhook fires BEFORE the success-persist. A transient DB error on that persist must
///      NOT be recorded as a delivery failure (the subscriber already received it) and must NOT
///      re-POST identical bytes. A genuine send failure MUST still be recorded. A stable
///      <c>X-ProcuLink-Delivery-Id</c> header lets a well-behaved subscriber dedupe a re-POST.
/// A4b: the consecutive-failure count must increment atomically (no lost update under concurrent
///      failures) and exactly once per logical failure even if a persist throws mid-flight.
/// </summary>
public class FireIntegrationTriggerJobReliabilityTests
{
    // ── infra helpers ────────────────────────────────────────────────────────
    private static DbContextOptions<ProcuLinkDbContext> Options(string name) =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

    private static ProcuLinkDbContext NewDb(string name) => new(Options(name));
    private static ProcuLinkDbContext NewDb() => NewDb(Guid.NewGuid().ToString());

    private static OutboundRequestGuard PermissiveGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static async Task<Guid> SeedAsync(ProcuLinkDbContext db, int failureCount = 0)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Reliability Org",
            Slug = $"rel-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });

        var subId = Guid.NewGuid();
        db.IntegrationSubscriptions.Add(new IntegrationSubscription
        {
            Id = subId,
            OrganisationId = orgId,
            Platform = "custom",
            EventType = "order.delivered",
            TargetUrl = "https://hooks.example.com/webhook",
            EncryptedSecret = null,
            IsActive = true,
            FailureCount = failureCount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subId;
    }

    // ── test doubles ─────────────────────────────────────────────────────────

    /// <summary>Job whose send path returns a fixed status through an in-memory handler.</summary>
    private sealed class FixedStatusJob : FireIntegrationTriggerJob
    {
        private readonly HttpStatusCode _status;
        public FixedStatusJob(ProcuLinkDbContext db, HttpStatusCode status)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(),
                   NullLogger<FireIntegrationTriggerJob>.Instance)
            => _status = status;

        internal override HttpClient CreateSendClient() => new(new FixedStatusHandler(_status));
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("body") });
    }

    /// <summary>Job whose send path throws — models a connection reset / DNS / TLS / timeout.</summary>
    private sealed class ThrowingSendJob : FireIntegrationTriggerJob
    {
        public ThrowingSendJob(ProcuLinkDbContext db)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(),
                   NullLogger<FireIntegrationTriggerJob>.Instance)
        { }

        internal override HttpClient CreateSendClient() => new(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused (simulated)");
    }

    /// <summary>Job that records the outgoing request headers so we can assert delivery-id stability.</summary>
    private sealed class CapturingJob : FireIntegrationTriggerJob
    {
        public List<(string? DeliveryId, string? EventId)> Sent { get; } = new();
        public CapturingJob(ProcuLinkDbContext db)
            : base(db, new Moq.Mock<IHttpClientFactory>().Object, Enc(), PermissiveGuard(),
                   NullLogger<FireIntegrationTriggerJob>.Instance)
        { }

        internal override HttpClient CreateSendClient() => new(new CapturingHandler(Sent));
    }

    private sealed class CapturingHandler(List<(string? DeliveryId, string? EventId)> sink) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? delivery = request.Headers.TryGetValues("X-ProcuLink-Delivery-Id", out var d) ? d.FirstOrDefault() : null;
            string? evt      = request.Headers.TryGetValues("X-ProcuLink-Event-Id",    out var e) ? e.FirstOrDefault() : null;
            sink.Add((delivery, evt));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    /// <summary>DbContext whose FIRST <c>SaveChangesAsync</c> throws a transient error, then succeeds.</summary>
    private sealed class FlakyOnFirstSaveDb : ProcuLinkDbContext
    {
        private int _saves;
        public FlakyOnFirstSaveDb(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _saves) == 1)
                throw new DbUpdateException("transient persist failure (simulated)");
            return base.SaveChangesAsync(ct);
        }
    }

    // ── A4: delivered webhook, success-persist fails ──────────────────────────

    [Fact]
    public async Task DeliveredWebhook_SuccessPersistFails_DoesNotRecordFailureAndDoesNotThrow()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid subId;
        await using (var seed = NewDb(dbName))
            subId = await SeedAsync(seed);

        // The delivered (2xx) success reset is the first SaveChanges → it throws. The webhook was
        // already delivered, so this must NOT become a recorded failure and must NOT rethrow (a
        // rethrow makes Hangfire retry and re-POST the identical webhook).
        await using var flaky = new FlakyOnFirstSaveDb(Options(dbName));
        var job = new FixedStatusJob(flaky, HttpStatusCode.OK);

        var act = async () => await job.ExecuteCoreAsync(subId, "{\"order_id\":\"po-1\"}", isFinalAttempt: true, default);
        await act.Should().NotThrowAsync("a webhook that was delivered must never surface a persist error as a job failure");

        await using var verify = NewDb(dbName);
        var sub = await verify.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(0, "a delivered webhook must never be recorded as a failed delivery");
        sub.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RetriedDelivery_SendsStableDeliveryIdHeaderAcrossRetries()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db);
        var job = new CapturingJob(db);

        const string payload = "{\"order_id\":\"po-42\",\"status\":\"delivered\"}";

        // Two runs with the SAME (subscriptionId, payload) model a Hangfire retry re-POSTing the
        // identical bytes — the delivery id the subscriber dedupes on must be identical.
        await job.ExecuteCoreAsync(subId, payload, isFinalAttempt: false, default);
        await job.ExecuteCoreAsync(subId, payload, isFinalAttempt: false, default);

        // A different event payload must yield a DIFFERENT delivery id (uniqueness per delivery).
        await job.ExecuteCoreAsync(subId, "{\"order_id\":\"po-99\"}", isFinalAttempt: false, default);

        job.Sent.Should().HaveCount(3);
        var first = job.Sent[0];
        var retry = job.Sent[1];
        var other = job.Sent[2];

        first.DeliveryId.Should().NotBeNullOrEmpty("every webhook must carry a delivery id header");
        Guid.TryParse(first.DeliveryId, out _).Should().BeTrue("the delivery id should be a stable opaque id");
        retry.DeliveryId.Should().Be(first.DeliveryId, "a Hangfire retry re-POSTs the SAME delivery id so the subscriber can dedupe");
        other.DeliveryId.Should().NotBe(first.DeliveryId, "a distinct event payload gets a distinct delivery id");

        first.EventId.Should().NotBeNullOrEmpty("an event id header should also be sent");
        retry.EventId.Should().Be(first.EventId, "the event id is stable for the same event across retries");
    }

    [Fact]
    public async Task GenuineSendException_OnFinalAttempt_RecordsFailure()
    {
        await using var db = NewDb();
        var subId = await SeedAsync(db);
        var job = new ThrowingSendJob(db);

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>("a real send failure must fail the job so Hangfire retries");

        var sub = await db.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(1, "a genuine send failure (exception) IS a real delivery failure and must be recorded");
    }

    // ── A4b: atomic increment / no double-count ───────────────────────────────

    [Fact]
    public async Task FinalFailure_WhenPersistThrowsOnce_IsNeverDoubleCounted()
    {
        // A transient error on the failure-persist must not cause the SAME failure to be counted
        // twice — the old catch re-invoked RecordFailure, so when its first save threw and the
        // retry-in-catch save succeeded the count jumped by 2. The fixed path records the failure
        // exactly once with no recursive catch, so a persist hiccup leaves the count at 0 (Hangfire
        // will re-run the whole job) — never at 2.
        var dbName = Guid.NewGuid().ToString();
        Guid subId;
        await using (var seed = NewDb(dbName))
            subId = await SeedAsync(seed);

        await using var flaky = new FlakyOnFirstSaveDb(Options(dbName));
        var job = new FixedStatusJob(flaky, HttpStatusCode.InternalServerError);

        var act = async () => await job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default);
        await act.Should().ThrowAsync<Exception>();

        await using var verify = NewDb(dbName);
        var sub = await verify.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().BeLessThanOrEqualTo(1,
            "one logical failure must never be double-counted, even if a persist throws mid-flight");
    }

    [Fact]
    public async Task TwoSequentialFinalFailures_EachCountedOnce()
    {
        // Two independent logical failures (fresh contexts, each reading current state) must land at
        // base + 2 — a regression guard on the per-failure counting that runs on any provider.
        var dbName = Guid.NewGuid().ToString();
        Guid subId;
        await using (var seed = NewDb(dbName))
            subId = await SeedAsync(seed);

        await using (var ctx1 = NewDb(dbName))
        {
            var job = new FixedStatusJob(ctx1, HttpStatusCode.InternalServerError);
            await ((Func<Task>)(() => job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var ctx2 = NewDb(dbName))
        {
            var job = new FixedStatusJob(ctx2, HttpStatusCode.InternalServerError);
            await ((Func<Task>)(() => job.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = NewDb(dbName);
        var sub = await verify.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(2, "two separate logical failures each count exactly once");
    }

    /// <summary>
    /// FAITHFUL lost-update test. The fix (<c>FailureCount = FailureCount + 1</c>) is a RELATIONAL
    /// guarantee — the InMemory provider cannot translate a relative <c>ExecuteUpdate</c> — so it
    /// runs against a throwaway database on the local dev Postgres. Both jobs read the same base
    /// value BEFORE either persists (the classic interleave): a load-modify-save bump persists
    /// base+1 from BOTH and loses one; the atomic relative UPDATE persists base+2.
    ///
    /// <para>Until WP-02 this opened with <c>if (admin is null) return;</c>, so on every machine
    /// and every CI run without a Postgres on :5435 it reported <b>Passed</b> having proved
    /// nothing about the concurrency guarantee it exists for. <see cref="LocalPostgresRequiredFact"/>
    /// turns that into a declared skip with a reason.</para>
    /// </summary>
    [LocalPostgresRequiredFact]
    public async Task TwoConcurrentFinalFailures_OnPostgres_IncrementFailureCountByExactlyTwo()
    {
        // The attribute already established that :5435 answers; a failure to connect here is a
        // real failure, not a reason to go quiet.
        var admin = await OpenPostgresAdminAsync();

        var dbName = $"plk_webhook_{Guid.NewGuid():N}";
        try
        {
            await ExecAsync(admin, $"CREATE DATABASE \"{dbName}\"");

            var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseNpgsql($"Host=localhost;Port=5435;Username=postgres;Password=postgres;Database={dbName}")
                .Options;

            Guid subId;
            await using (var db = new ProcuLinkDbContext(opts))
            {
                await db.Database.EnsureCreatedAsync();
                subId = await SeedAsync(db, failureCount: 1);
            }

            await using var ctxA = new ProcuLinkDbContext(opts);
            await using var ctxB = new ProcuLinkDbContext(opts);
            _ = await ctxA.IntegrationSubscriptions.FirstAsync(s => s.Id == subId);
            _ = await ctxB.IntegrationSubscriptions.FirstAsync(s => s.Id == subId);

            var jobA = new FixedStatusJob(ctxA, HttpStatusCode.InternalServerError);
            var jobB = new FixedStatusJob(ctxB, HttpStatusCode.InternalServerError);

            await ((Func<Task>)(() => jobA.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
                .Should().ThrowAsync<InvalidOperationException>();
            await ((Func<Task>)(() => jobB.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
                .Should().ThrowAsync<InvalidOperationException>();

            await using var verify = new ProcuLinkDbContext(opts);
            var sub = await verify.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
            sub.FailureCount.Should().Be(3, "two concurrent failures from base 1 land at 3 — the atomic relative increment loses none");
            sub.IsActive.Should().BeFalse("reaching the 3-failure threshold deactivates the subscription");
        }
        finally
        {
            try
            {
                await ExecAsync(admin,
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}'");
                await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{dbName}\"");
            }
            catch { /* best-effort cleanup */ }
            await admin.DisposeAsync();
        }
    }

    /// <summary>
    /// Opens the dev Postgres admin connection. Deliberately NOT try/catch-to-null: swallowing the
    /// connection error is what let this test report Passed without a database. Reachability is
    /// decided once, up front, by <see cref="LocalPostgresProbe"/>.
    /// </summary>
    private static async Task<Npgsql.NpgsqlConnection> OpenPostgresAdminAsync()
    {
        var conn = new Npgsql.NpgsqlConnection(LocalPostgresProbe.AdminConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(Npgsql.NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
