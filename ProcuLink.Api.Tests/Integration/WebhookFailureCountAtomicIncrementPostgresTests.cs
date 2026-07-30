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
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Finding A4b — proves the consecutive-failure count survives an interleave with NO LOST UPDATE on
/// REAL Postgres, where the fix's relative <c>ExecuteUpdateAsync</c>
/// (<c>FailureCount = FailureCount + 1</c>) actually runs. The EF InMemory provider cannot translate
/// a relative <c>ExecuteUpdate</c>, so its sibling in
/// <c>ProcuLink.Infrastructure.Tests.Jobs.FireIntegrationTriggerJobReliabilityTests</c> only ever
/// exercises the load-modify-save fallback branch — the relational guarantee that ships to
/// production would otherwise be entirely untested.
///
/// <para>Both jobs read the same base value BEFORE either persists (the classic interleave): a
/// load-modify-save bump persists base+1 from BOTH and loses one; the atomic relative UPDATE
/// persists base+2. Seeding at 1 makes the correct result 3 — which is also the deactivation
/// threshold, so the lost update would additionally leave a dead subscription live.</para>
///
/// <para>This lived in <c>FireIntegrationTriggerJobReliabilityTests</c> gated on a local dev
/// Postgres at <c>localhost:5435</c>, which CI has never had — so the one test proving the
/// relational guarantee never ran there. Docker-gated via Testcontainers instead; ubuntu-latest
/// runners have Docker, so it runs on every CI build.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class WebhookFailureCountAtomicIncrementPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_whfail_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    // ── the test ─────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task TwoConcurrentFinalFailures_OnPostgres_IncrementFailureCountByExactlyTwo()
    {
        Guid subId;
        await using (var db = NewContext())
            subId = await SeedAsync(db, failureCount: 1);

        await using var ctxA = NewContext();
        await using var ctxB = NewContext();

        // Both contexts load the row BEFORE either job persists, so each tracked entity holds the
        // same stale FailureCount=1. Under a load-modify-save bump both would write 2.
        _ = await ctxA.IntegrationSubscriptions.FirstAsync(s => s.Id == subId);
        _ = await ctxB.IntegrationSubscriptions.FirstAsync(s => s.Id == subId);

        var jobA = new FixedStatusJob(ctxA, HttpStatusCode.InternalServerError);
        var jobB = new FixedStatusJob(ctxB, HttpStatusCode.InternalServerError);

        await ((Func<Task>)(() => jobA.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
            .Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => jobB.ExecuteCoreAsync(subId, "{}", isFinalAttempt: true, default)))
            .Should().ThrowAsync<InvalidOperationException>();

        await using var verify = NewContext();
        var sub = await verify.IntegrationSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subId);
        sub.FailureCount.Should().Be(3, "two concurrent failures from base 1 land at 3 — the atomic relative increment loses none");
        sub.IsActive.Should().BeFalse("reaching the 3-failure threshold deactivates the subscription");
    }

    // ── seeding + test doubles ───────────────────────────────────────────────

    private static async Task<Guid> SeedAsync(ProcuLinkDbContext db, int failureCount)
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
}
