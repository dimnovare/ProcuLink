using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// Covers the auto-provision throttle (P1-5): a single throttle key (client IP /
/// email-domain) may mint at most a bounded number of new pilot orgs within a rolling
/// window. A legitimate first login provisions once and is never affected; a script
/// hammering from one IP is cut off after the cap, and the window re-opens over time.
/// </summary>
public class TenantResolutionMiddlewareProvisionThrottleTests
{
    private const int MaxProvisionsPerWindow = 5; // mirrors the middleware constant

    private static DbContextOptions<ProcuLinkDbContext> NewOptions() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

    /// <summary>
    /// Runs the middleware exactly as the pipeline does: with a FRESH, never-queried
    /// request-scoped context. The middleware arms that context with ScopeToOrganisation at the
    /// end of InvokeAsync, and that refuses a context which has already resolved its model by
    /// running a query — so the request context can never double as a seed or assert context.
    /// </summary>
    private static async Task RunAsync(
        TenantResolutionMiddleware middleware,
        HttpContext ctx,
        DbContextOptions<ProcuLinkDbContext> options,
        FakeAnalyticsService analytics)
    {
        await using var requestDb = new ProcuLinkDbContext(options);
        await middleware.InvokeAsync(ctx, requestDb, analytics, options);
    }

    /// <summary>Counts organisations on a separate, unscoped context over the same store.</summary>
    private static async Task<int> CountOrgsAsync(DbContextOptions<ProcuLinkDbContext> options)
    {
        await using var db = new ProcuLinkDbContext(options);
        return await db.Organisations.CountAsync();
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

    [Fact]
    public async Task NormalFirstLogin_FromOneIp_StillProvisions()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = NewRequest("203.0.113.7", "org_NEWUSER", "user_NEWUSER");
        await RunAsync(middleware, ctx, options, analytics);

        // One org created, one org_created event — behaviour unchanged for a real new user.
        Assert.Equal(1, await CountOrgsAsync(options));
        Assert.Single(analytics.CapturedEvents);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    [Fact]
    public async Task ScriptFarmingTrials_FromOneIp_IsThrottledAfterCap()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        // Single middleware instance so the in-memory window persists across requests,
        // mirroring the UseMiddleware<> singleton in production.
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        const string attackerIp = "198.51.100.42";

        // Distinct identities (fresh Clerk org each time) from the SAME IP.
        for (var i = 0; i < MaxProvisionsPerWindow + 4; i++)
        {
            var ctx = NewRequest(attackerIp, $"org_FARM_{i}", $"user_FARM_{i}");
            await RunAsync(middleware, ctx, options, analytics);
        }

        // Only the cap is honoured despite many distinct identities from one IP.
        Assert.Equal(MaxProvisionsPerWindow, await CountOrgsAsync(options));
        Assert.Equal(MaxProvisionsPerWindow, analytics.CapturedEvents.Count);
    }

    [Fact]
    public async Task ThrottledRequest_FailsClosed_NoTenantResolved_PipelineContinues()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var nextCalls = 0;
        var middleware = new TenantResolutionMiddleware(
            next: _ => { nextCalls++; return Task.CompletedTask; },
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        const string attackerIp = "198.51.100.43";

        // Exhaust the window.
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
        {
            var ok = NewRequest(attackerIp, $"org_OK_{i}", $"user_OK_{i}");
            await RunAsync(middleware, ok, options, analytics);
        }

        // The next distinct identity is throttled.
        var blocked = NewRequest(attackerIp, "org_BLOCKED", "user_BLOCKED");
        await RunAsync(middleware, blocked, options, analytics);

        // No tenant resolved for the throttled request, but the pipeline still ran
        // (downstream [Authorize] / tenant-scoped controllers fail closed).
        Assert.False(blocked.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        // next() invoked once per request (5 provisioned + 1 throttled = 6).
        Assert.Equal(MaxProvisionsPerWindow + 1, nextCalls);
        // No extra org / event for the throttled call.
        Assert.Equal(MaxProvisionsPerWindow, await CountOrgsAsync(options));
    }

    [Fact]
    public async Task DifferentIps_EachGetOwnBudget_NotThrottledAcrossIps()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        // Two genuinely different users behind two different IPs both provision fine,
        // even though together they exceed a single IP's cap.
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
        {
            var a = NewRequest("203.0.113.10", $"org_A_{i}", $"user_A_{i}");
            await RunAsync(middleware, a, options, analytics);
        }
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
        {
            var b = NewRequest("203.0.113.11", $"org_B_{i}", $"user_B_{i}");
            await RunAsync(middleware, b, options, analytics);
        }

        Assert.Equal(MaxProvisionsPerWindow * 2, await CountOrgsAsync(options));
    }

    [Fact]
    public async Task ManyDistinctKeys_AfterWindowExpiry_StoreDoesNotRetainExpiredEntries()
    {
        // Guards the unbounded-dictionary DoS: the throttle store is keyed by client IP /
        // email-domain. A flood of DISTINCT keys (botnet / NAT churn / spoofed XFF) used to
        // accumulate forever — the per-key sliding window pruned timestamps but never the
        // dictionary keys themselves. The periodic sweep must reclaim entries whose window
        // has fully aged out so the store cannot grow without limit.
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();

        var clock = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance,
            utcNow: () => clock);

        // Cross the sweep cadence (SweepEveryNCalls = 256) comfortably so a sweep runs.
        const int distinctKeys = 600;

        // 1. A burst of distinct first-logins, one per unique IP, at t0. Each adds a key.
        for (var i = 0; i < distinctKeys; i++)
        {
            // Distinct, valid IPv4 addresses → distinct throttle keys.
            var ip = $"10.{(i >> 16) & 0xFF}.{(i >> 8) & 0xFF}.{i & 0xFF}";
            var ctx = NewRequest(ip, $"org_FLOOD_{i}", $"user_FLOOD_{i}");
            await RunAsync(middleware, ctx, options, analytics);
        }

        Assert.True(
            middleware.TrackedKeyCount > 0,
            "Sanity: distinct first-logins should have populated the throttle store.");

        // 2. Advance well past the rolling window so every entry's window has aged out.
        clock = clock.AddMinutes(11);

        // 3. More distinct first-logins after expiry. These trigger the opportunistic
        //    sweep, which must evict the now-stale t0 entries.
        for (var i = 0; i < distinctKeys; i++)
        {
            var ip = $"10.{(i >> 16) & 0xFF}.{(i >> 8) & 0xFF}.{i & 0xFF}";
            var ctx = NewRequest(ip, $"org_FLOOD2_{i}", $"user_FLOOD2_{i}");
            await RunAsync(middleware, ctx, options, analytics);
        }

        // The store must NOT retain the original ~600 expired entries on top of the new
        // ~600. If pruning worked, only the (still-live) post-expiry keys remain — which is
        // at most `distinctKeys`, never the unbounded sum. This is the regression assertion:
        // the dictionary stays bounded instead of growing without limit.
        Assert.True(
            middleware.TrackedKeyCount <= distinctKeys,
            $"Throttle store retained {middleware.TrackedKeyCount} entries; expired entries " +
            $"were not pruned (expected <= {distinctKeys}).");
    }

    [Fact]
    public async Task WindowReopens_AfterRollingWindowElapses()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();

        var clock = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance,
            utcNow: () => clock);

        const string ip = "198.51.100.50";

        // Fill the window at t0.
        for (var i = 0; i < MaxProvisionsPerWindow; i++)
        {
            var ctx = NewRequest(ip, $"org_T0_{i}", $"user_T0_{i}");
            await RunAsync(middleware, ctx, options, analytics);
        }
        Assert.Equal(MaxProvisionsPerWindow, await CountOrgsAsync(options));

        // Immediately after, the next is throttled.
        var blocked = NewRequest(ip, "org_T0_BLOCKED", "user_T0_BLOCKED");
        await RunAsync(middleware, blocked, options, analytics);
        Assert.Equal(MaxProvisionsPerWindow, await CountOrgsAsync(options));

        // Advance past the rolling window — the old timestamps age out.
        clock = clock.AddMinutes(11);

        var afterWindow = NewRequest(ip, "org_T1", "user_T1");
        await RunAsync(middleware, afterWindow, options, analytics);

        Assert.Equal(MaxProvisionsPerWindow + 1, await CountOrgsAsync(options));
        Assert.True(afterWindow.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }
}
