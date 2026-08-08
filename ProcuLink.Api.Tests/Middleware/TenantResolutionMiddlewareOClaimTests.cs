using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Middleware;

/// <summary>
/// Clerk v2 session tokens (payload claim "v":2) do NOT carry the legacy
/// top-level org_id / org_slug claims. Org info is instead packed into a compact
/// "o" claim — a JSON object: { "id": "org_…", "rol": "...", "slg": "..." }.
///
/// The .NET JsonWebTokenHandler (MapInboundClaims=false) materialises a JSON-object
/// payload value as a single Claim whose type is "o" and whose .Value is the JSON
/// STRING. These tests build that exact shape (new Claim("o", "{...}")) to prove the
/// middleware parses o.id / o.slg and runs the SAME adopt / provision flow it runs
/// for the v1 org_id claim — without which org resolution was inert against real
/// prod tokens (every prod org stuck legacy sub-keyed; new users fail closed).
/// </summary>
public class TenantResolutionMiddlewareOClaimTests
{
    private static DbContextOptions<ProcuLinkDbContext> NewOptions() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

    /// <summary>
    /// A context over the test's single in-memory store. Seeding, the request context handed to
    /// InvokeAsync, and the post-invoke assertions each get their OWN instance: the middleware
    /// arms the request context with ScopeToOrganisation at the end of InvokeAsync, and that
    /// refuses a context which has already resolved its model by running a query.
    /// </summary>
    private static ProcuLinkDbContext NewDb(DbContextOptions<ProcuLinkDbContext> options) =>
        new(options);

    private static TenantResolutionMiddleware NewMiddleware() =>
        new(next: _ => Task.CompletedTask,
            logger: NullLogger<TenantResolutionMiddleware>.Instance);

    private static DefaultHttpContext WithClaims(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        return ctx;
    }

    private static Organisation SeedOrg(string clerkOrgId, string name)
    {
        var now = DateTime.UtcNow;
        return new Organisation
        {
            Id             = Guid.NewGuid(),
            ClerkOrgId     = clerkOrgId,
            Name           = name,
            Slug           = name.ToLowerInvariant() + "-1234",
            Plan           = "operations",
            AccountStatus  = "active",
            CreatedAt      = now,
            TrialStartedAt = now,
        };
    }

    // The exact shape the JsonWebTokenHandler produces for the v2 "o" object claim:
    // a single claim of type "o" whose value is the JSON string.
    private static Claim OClaim(string id, string rol, string slg) =>
        new("o", $"{{\"id\":\"{id}\",\"rol\":\"{rol}\",\"slg\":\"{slg}\"}}");

    // 1. v2 o claim + user has a legacy sub org → ADOPTS (re-keys the sub org), resolves.
    [Fact]
    public async Task InvokeAsync_OClaim_WithMatchingPersonalTenant_AdoptsAndRekeys()
    {
        var options = NewOptions();
        var legacy = SeedOrg("user_U", "PersonalWorkspace");
        await using (var seed = NewDb(options))
        {
            seed.Organisations.Add(legacy);
            await seed.SaveChangesAsync();
        }
        var legacyId = legacy.Id;

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            OClaim("org_REAL", "admin", "acme"),
            new Claim("sub", "user_U"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        // No new row — the SAME row was re-keyed to the org_ id from o.id.
        Assert.Equal(1, await db.Organisations.CountAsync());
        var adopted = await db.Organisations.AsNoTracking()
            .SingleAsync(o => o.Id == legacyId);
        Assert.Equal("org_REAL", adopted.ClerkOrgId);

        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(legacyId, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);

        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_adopted", ev.EventName);
        Assert.Equal("user_U", ev.UserId);
        Assert.DoesNotContain(analytics.CapturedEvents, e => e.EventName == "org_created");
    }

    // 2. v2 o claim + no sub org → fresh-provisions an org_REAL org (org_created).
    [Fact]
    public async Task InvokeAsync_OClaim_NoPersonalTenant_ProvisionsFresh()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            OClaim("org_REAL", "admin", "fresh-co"),
            new Claim("sub", "user_NEW"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(1, await db.Organisations.CountAsync());
        var created = await db.Organisations.AsNoTracking().SingleAsync();
        Assert.Equal("org_REAL", created.ClerkOrgId);
        // Slug from o.slg flows into the provisioned org name → slug.
        Assert.StartsWith("fresh-co", created.Slug);

        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_created", ev.EventName);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(created.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }

    // 3. v1 back-compat: an explicit org_id claim still works — the o fallback does
    //    not break v1 (explicit claims win, o is only consulted when org_id is absent).
    [Fact]
    public async Task InvokeAsync_V1OrgId_StillProvisions_FallbackDoesNotBreakV1()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_LEGACY"),
            new Claim("org_slug", "legacy-co"),
            new Claim("sub", "user_V1"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(1, await db.Organisations.CountAsync());
        var created = await db.Organisations.AsNoTracking().SingleAsync();
        Assert.Equal("org_LEGACY", created.ClerkOrgId);

        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_created", ev.EventName);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    // 3b. Mixed: explicit v1 org_id present AND a v2 o claim with a DIFFERENT id —
    //     the explicit v1 claim must win (the fallback only fires when org_id is empty).
    [Fact]
    public async Task InvokeAsync_BothV1AndOClaim_V1OrgIdWins()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_FROM_V1"),
            OClaim("org_FROM_O", "admin", "other"),
            new Claim("sub", "user_M"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(1, await db.Organisations.CountAsync());
        var created = await db.Organisations.AsNoTracking().SingleAsync();
        Assert.Equal("org_FROM_V1", created.ClerkOrgId);
    }

    // 4a. Malformed o ("not-json") → no crash, treated as no org (sub-only path → fail closed).
    [Fact]
    public async Task InvokeAsync_MalformedOClaim_NotJson_FailsClosed_NoCrash()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("o", "not-json"),
            new Claim("sub", "user_BAD"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    // 4b. Empty o object ({}) → no id → treated as no org (sub-only path → fail closed).
    [Fact]
    public async Task InvokeAsync_EmptyOClaimObject_FailsClosed_NoCrash()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("o", "{}"),
            new Claim("sub", "user_EMPTY"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    // 4c. Empty o object but an existing legacy sub org → softened RESOLVE (no lockout).
    [Fact]
    public async Task InvokeAsync_EmptyOClaim_WithLegacySubOrg_SoftResolves()
    {
        var options = NewOptions();
        var legacy = SeedOrg("user_L", "LegacyTenant");
        await using (var seed = NewDb(options))
        {
            seed.Organisations.Add(legacy);
            await seed.SaveChangesAsync();
        }

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("o", "{}"),
            new Claim("sub", "user_L"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(1, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(legacy.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }

    // 5. o.id that is NOT org_-prefixed → ignored (does not satisfy the org_ gate),
    //    falls through to the sub-only path (fail closed here, no legacy org).
    [Fact]
    public async Task InvokeAsync_OClaim_NonOrgPrefixedId_FailsClosed()
    {
        var options = NewOptions();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            OClaim("not-an-org-id", "admin", "x"),
            new Claim("sub", "user_Z"));

        await using (var requestDb = NewDb(options))
        {
            await NewMiddleware().InvokeAsync(ctx, requestDb, analytics, options);
        }

        await using var db = NewDb(options);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }
}
