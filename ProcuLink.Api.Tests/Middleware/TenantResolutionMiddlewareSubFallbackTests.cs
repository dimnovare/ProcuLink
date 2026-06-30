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
/// The sub-fallback was removed: a request without a real Clerk ORGANISATION id
/// (org_…) must NOT silently provision a per-user "Personal workspace" tenant.
/// It resolves no tenant and fails closed downstream (same shape as the throttle
/// path). The frontend org gate forces org creation before any tenant-scoped call.
/// </summary>
public class TenantResolutionMiddlewareSubFallbackTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task InvokeAsync_SubOnly_NoOrgIdClaim_DoesNotProvision_FailsClosed()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user_LONELY"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    [Fact]
    public async Task InvokeAsync_NonOrgPrefixedTenantKey_DoesNotProvision()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", "1d3c9e7a-0000-4000-8000-000000000000"),
            new Claim("sub", "user_X"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    [Fact]
    public async Task InvokeAsync_RealOrgId_StillProvisions()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", "org_REAL_123"),
            new Claim("org_slug", "acme-co"),
            new Claim("sub", "user_abc"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, db, analytics);

        Assert.Equal(1, await db.Organisations.CountAsync());
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }
}
