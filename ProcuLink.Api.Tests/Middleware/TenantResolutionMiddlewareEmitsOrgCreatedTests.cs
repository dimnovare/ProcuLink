using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Infrastructure;
using ProcuLink.Api.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

public class TenantResolutionMiddlewareEmitsOrgCreatedTests
{
    [Fact]
    public async Task InvokeAsync_AutoProvisionsOrg_EmitsOrgCreatedEvent()
    {
        var dbOptions = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        // The request-scoped context the middleware arms. It must be fresh and never queried:
        // ScopeToOrganisation refuses a context that has already resolved its model.
        await using var requestDb = new ProcuLinkDbContext(dbOptions);

        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", "org_TEST_123"),
            new Claim("org_slug", "acme-co"),
            new Claim("sub", "user_TEST_abc"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, requestDb, analytics, dbOptions);

        Assert.Single(analytics.CapturedEvents);
        var ev = analytics.CapturedEvents[0];
        Assert.Equal("org_created", ev.EventName);
        Assert.Equal("user_TEST_abc", ev.UserId);
        Assert.Equal("pilot", ev.Properties["plan"]);
        Assert.Equal("signup_flow", ev.Properties["created_via"]);
        Assert.NotEqual(Guid.Empty, ev.OrgId);
    }

    [Fact]
    public async Task InvokeAsync_ExistingOrg_DoesNotEmitOrgCreated()
    {
        var dbOptions = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        // Seeding runs on its OWN context: a context that has queried can no longer be scoped,
        // so the one handed to InvokeAsync below must be untouched.
        await using (var seed = new ProcuLinkDbContext(dbOptions))
        {
            seed.Organisations.Add(new ProcuLink.Core.Entities.Organisation
            {
                Id = Guid.NewGuid(),
                ClerkOrgId = "org_EXISTING",
                Name = "Existing",
                Slug = "existing-1234",
                Plan = "pilot",
                AccountStatus = "trialing",
                CreatedAt = DateTime.UtcNow,
                TrialStartedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var requestDb = new ProcuLinkDbContext(dbOptions);

        var analytics = new FakeAnalyticsService();
        var middleware = new TenantResolutionMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("org_id", "org_EXISTING"),
            new Claim("sub", "user_EXISTING_abc"),
        }, authenticationType: "test"));

        await middleware.InvokeAsync(ctx, requestDb, analytics, dbOptions);

        Assert.Empty(analytics.CapturedEvents);
    }
}
