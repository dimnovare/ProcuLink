using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// Covers what a COLD database does to a brand-new organisation's first page load.
///
/// <para>Production runs on Neon, whose compute suspends when idle. The liveness probe is
/// deliberately dependency-free, so nothing keeps the database warm between bursts of traffic, and
/// the first request after a quiet period pays a cold start. Two separate things went wrong when
/// that happened, and both landed on the first screen a new customer sees:</para>
///
/// <list type="number">
///   <item>A transient connection fault during tenant resolution was not retried and not
///   recognised, so it surfaced downstream as
///   <c>UnauthorizedAccessException("Organisation not resolved")</c> — an authorization error
///   describing a database outage.</item>
///   <item>A first page load is a BURST of requests. While the winner's INSERT was still in
///   flight, every sibling also saw "no row", spent a provision reservation, and the ones past the
///   cap were failed closed — again surfacing as "Organisation not resolved", for users who had
///   done nothing wrong.</item>
/// </list>
/// </summary>
public class TenantResolutionMiddlewareColdDatabaseTests
{
    private const int MaxProvisionsPerWindow = 5; // mirrors the middleware constant

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fault that reports itself transient the way Npgsql does — via an <c>IsTransient</c>
    /// property. The middleware duck-types that property rather than referencing Npgsql, so this
    /// exercises the real detection path and not a test-only shortcut.
    /// </summary>
    private sealed class TransientByPropertyException : Exception
    {
        public TransientByPropertyException() : base("simulated transient database fault") { }
        public bool IsTransient => true;
    }

    /// <summary>
    /// The other detection route: SQLSTATE class 08, "connection exception". Pinned separately so
    /// a change that keeps one route working and silently drops the other still fails.
    /// </summary>
    private sealed class TransientBySqlStateException : Exception
    {
        public TransientBySqlStateException() : base("simulated connection failure") { }
        public string SqlState => "08006";
    }

    /// <summary>Throws on the first <paramref name="failures"/> saves, then lets saves through.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        private readonly int _failures;
        private readonly Func<Exception> _fault;
        public int Attempts { get; private set; }

        public FailingSaveInterceptor(int failures, Func<Exception> fault)
        {
            _failures = failures;
            _fault = fault;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= _failures)
                throw _fault();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static DbContextOptions<ProcuLinkDbContext> NewOptions(
        string store, SaveChangesInterceptor? interceptor = null)
    {
        var b = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(store);
        if (interceptor is not null) b.AddInterceptors(interceptor);
        return b.Options;
    }

    private static HttpContext NewRequest(string ip, string orgId, string sub)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", orgId),
            new Claim("org_slug", "acme-co"),
            new Claim("sub", sub),
        }, authenticationType: "test"));
        return ctx;
    }

    private static async Task RunAsync(
        TenantResolutionMiddleware middleware,
        HttpContext ctx,
        DbContextOptions<ProcuLinkDbContext> options,
        FakeAnalyticsService analytics)
    {
        await using var requestDb = new ProcuLinkDbContext(options);
        await middleware.InvokeAsync(ctx, requestDb, analytics, options);
    }

    private static async Task<int> CountOrgsAsync(DbContextOptions<ProcuLinkDbContext> options)
    {
        await using var db = new ProcuLinkDbContext(options);
        return await db.Organisations.CountAsync();
    }

    private static bool Stamped(HttpContext ctx) =>
        ctx.Items.TryGetValue(CurrentTenantService.Items.OrganisationId, out var v)
        && v is Guid g && g != Guid.Empty;

    // ── 1. A transient fault is retried, not surfaced ───────────────────────────────────────────

    [Fact]
    public async Task ColdStart_ThatClearsOnRetry_ProvisionsNormally()
    {
        var store = Guid.NewGuid().ToString();
        // Two failures then success: inside the three-attempt budget.
        var interceptor = new FailingSaveInterceptor(2, () => new TransientByPropertyException());
        var options = NewOptions(store, interceptor);
        var analytics = new FakeAnalyticsService();

        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = NewRequest("203.0.113.10", "org_COLD", "user_COLD");
        await RunAsync(middleware, ctx, options, analytics);

        Assert.Equal(3, interceptor.Attempts);          // failed twice, succeeded on the third
        Assert.Equal(1, await CountOrgsAsync(options)); // exactly one org, not one per attempt
        Assert.True(Stamped(ctx));
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ColdStart_ReportedBySqlState_IsAlsoRetried()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailingSaveInterceptor(1, () => new TransientBySqlStateException());
        var options = NewOptions(store, interceptor);

        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = NewRequest("203.0.113.11", "org_SQLSTATE", "user_SQLSTATE");
        await RunAsync(middleware, ctx, options, new FakeAnalyticsService());

        Assert.Equal(2, interceptor.Attempts);
        Assert.True(Stamped(ctx));
    }

    // ── 2. A fault that never clears is reported honestly ───────────────────────────────────────

    [Fact]
    public async Task DatabaseUnreachable_Answers503_AndDoesNotRunTheRestOfThePipeline()
    {
        var store = Guid.NewGuid().ToString();
        // More failures than the retry budget, so the fault never clears.
        var interceptor = new FailingSaveInterceptor(99, () => new TransientByPropertyException());
        var options = NewOptions(store, interceptor);

        var nextCalled = false;
        var middleware = new TenantResolutionMiddleware(
            next: _ => { nextCalled = true; return Task.CompletedTask; },
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = NewRequest("203.0.113.12", "org_DOWN", "user_DOWN");
        await RunAsync(middleware, ctx, options, new FakeAnalyticsService());

        // The honest answer: "try again shortly", not "you are not authorized".
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Equal("2", ctx.Response.Headers.RetryAfter.ToString());

        // Fail CLOSED: no tenant resolved, and the pipeline never ran, so nothing downstream can
        // read or write another organisation's data on an unresolved request.
        Assert.False(Stamped(ctx));
        Assert.False(nextCalled);
        Assert.Equal(3, interceptor.Attempts); // bounded: three attempts, not an unbounded loop
    }

    [Fact]
    public async Task ANonTransientFault_IsNotSwallowedAsA503()
    {
        var store = Guid.NewGuid().ToString();
        var interceptor = new FailingSaveInterceptor(
            99, () => new InvalidOperationException("a real bug, not a cold start"));
        var options = NewOptions(store, interceptor);

        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = NewRequest("203.0.113.13", "org_BUG", "user_BUG");

        // A genuine defect must still fail loudly. If this ever starts returning 503 instead, the
        // transient predicate has grown too broad and is hiding real errors behind a retry banner.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync(middleware, ctx, options, new FakeAnalyticsService()));
        Assert.Contains("a real bug", ex.ToString());
        Assert.Equal(1, interceptor.Attempts); // not retried
    }

    // ── 3. The burst: a throttled request may still RESOLVE, it just may not CREATE ─────────────

    [Fact]
    public async Task AThrottledRequest_ResolvesAnOrganisationASiblingCreatedMeanwhile()
    {
        var store = Guid.NewGuid().ToString();
        var options = NewOptions(store);
        var analytics = new FakeAnalyticsService();

        // The clock seam is called first thing inside the throttle check. Arming it lets a sibling
        // request "win" at exactly the moment this request is refused a reservation — which is the
        // real interleaving a cold start produces, made deterministic.
        var armed = false;
        var winner = Guid.NewGuid();
        Func<DateTime> clock = () =>
        {
            if (armed)
            {
                armed = false;
                using var db = new ProcuLinkDbContext(options);
                db.Organisations.Add(new Organisation
                {
                    Id = winner,
                    ClerkOrgId = "org_BURST",
                    Name = "burst",
                    Slug = "burst-0001",
                    Plan = "pilot",
                    AccountStatus = "trialing",
                    CreatedAt = DateTime.UtcNow,
                    TrialStartedAt = DateTime.UtcNow,
                    TrialEndsAt = DateTime.UtcNow.AddDays(14),
                });
                db.SaveChanges();
            }
            return DateTime.UtcNow;
        };

        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance,
            utcNow: clock);

        const string ip = "198.51.100.77";

        // Spend the whole window on other organisations, so the next request is certain to be
        // refused a reservation.
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
        {
            var warm = NewRequest(ip, $"org_OTHER{i}", $"user_OTHER{i}");
            await RunAsync(middleware, warm, options, analytics);
            Assert.True(Stamped(warm));
        }

        armed = true;
        var ctx = NewRequest(ip, "org_BURST", "user_BURST");
        await RunAsync(middleware, ctx, options, analytics);

        // The row existed by the time we were refused, so this request belongs to it. Before the
        // fix it left here unstamped and became "Organisation not resolved" downstream.
        Assert.True(Stamped(ctx));
        Assert.Equal(winner, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);

        // Resolving is not creating: the throttled request minted nothing, and emitted no
        // org_created event for a row it did not create.
        Assert.Equal(MaxProvisionsPerWindow + 1, await CountOrgsAsync(options));
        Assert.Equal(MaxProvisionsPerWindow, analytics.CapturedEvents.Count(e => e.EventName == "org_created"));
    }

    [Fact]
    public async Task AThrottledRequest_WithNoOrganisationToResolve_StillFailsClosed()
    {
        var store = Guid.NewGuid().ToString();
        var options = NewOptions(store);
        var analytics = new FakeAnalyticsService();

        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        const string attackerIp = "198.51.100.66";
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
            await RunAsync(middleware, NewRequest(attackerIp, $"org_FARM{i}", $"user_FARM{i}"), options, analytics);

        // A script minting fresh Clerk identities finds no row to resolve, so the re-read added for
        // the burst case gives it nothing: the trial-farming door the throttle exists to shut is
        // still shut.
        var ctx = NewRequest(attackerIp, "org_FARM_OVER", "user_FARM_OVER");
        await RunAsync(middleware, ctx, options, analytics);

        Assert.False(Stamped(ctx));
        Assert.Equal(MaxProvisionsPerWindow, await CountOrgsAsync(options));
    }
}
