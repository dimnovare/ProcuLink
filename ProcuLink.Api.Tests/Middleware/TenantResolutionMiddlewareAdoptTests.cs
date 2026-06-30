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
/// adopt-on-create + softened-resolve. The production base is 100% legacy
/// "sub-keyed" orgs (clerk_org_id = a Clerk user id "user_…"). A hard cutover
/// that only resolves real "org_…" ids would lock out every existing customer.
///
/// New behaviour:
///   - real org_ present and a matching org exists → resolve it.
///   - real org_ present, NO match, but the authenticated user's OWN legacy
///     personal tenant (ClerkOrgId == sub) exists → ADOPT: re-key that row to
///     the new org_ id (same row, data, plan, slug). Emit org_adopted, NOT
///     org_created.
///   - real org_ present, no match, no personal tenant for this sub → PROVISION
///     a fresh org (existing throttle + org_created path).
///   - NO org_ but sub present and a legacy org with ClerkOrgId == sub exists →
///     softened RESOLVE (no lockout for users still in the transition window).
///   - NO org_, sub present, no legacy org → fail closed (NEVER mint a new
///     sub-keyed tenant).
///
/// Security invariant: ADOPT may only re-key the org whose ClerkOrgId == sub of
/// the CURRENTLY authenticated user — a user can only adopt their OWN personal
/// tenant, never another user's row.
/// </summary>
public class TenantResolutionMiddlewareAdoptTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

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

    // 1. ADOPT: legacy personal tenant (ClerkOrgId == sub) is re-keyed to the new org_.
    [Fact]
    public async Task InvokeAsync_NewOrg_WithMatchingPersonalTenant_AdoptsAndRekeys()
    {
        await using var db = NewDb();
        var legacy = SeedOrg("user_U", "PersonalWorkspace");
        db.Organisations.Add(legacy);
        await db.SaveChangesAsync();
        var legacyId = legacy.Id;

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_NEW"),
            new Claim("org_slug", "team-co"),
            new Claim("sub", "user_U"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        // No new row — the SAME row was re-keyed.
        Assert.Equal(1, await db.Organisations.CountAsync());
        var adopted = await db.Organisations.AsNoTracking()
            .SingleAsync(o => o.Id == legacyId);
        Assert.Equal("org_NEW", adopted.ClerkOrgId);

        // Items resolves to that same row.
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(legacyId, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);

        // org_adopted (NOT org_created).
        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_adopted", ev.EventName);
        Assert.Equal("user_U", ev.UserId);
        Assert.Equal("personal_workspace", ev.Properties["from"]);
        Assert.DoesNotContain(analytics.CapturedEvents, e => e.EventName == "org_created");
    }

    // 2. PROVISION FRESH: real org_, no personal tenant for this sub.
    [Fact]
    public async Task InvokeAsync_NewOrg_NoPersonalTenant_ProvisionsFresh()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_FRESH"),
            new Claim("org_slug", "fresh-co"),
            new Claim("sub", "user_NEW"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        Assert.Equal(1, await db.Organisations.CountAsync());
        var created = await db.Organisations.AsNoTracking().SingleAsync();
        Assert.Equal("org_FRESH", created.ClerkOrgId);

        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_created", ev.EventName);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(created.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }

    // 3. EXISTING org_ resolves directly — no adopt, no provision, no analytics.
    [Fact]
    public async Task InvokeAsync_ExistingOrgId_Resolves_NoAdopt_NoAnalytics()
    {
        await using var db = NewDb();
        var existing = SeedOrg("org_EXIST", "Existing");
        db.Organisations.Add(existing);
        await db.SaveChangesAsync();

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_EXIST"),
            new Claim("sub", "user_x"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        Assert.Equal(1, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(existing.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }

    // 4. SOFTENED RESOLVE: sub-only login (no org_id) with an existing legacy sub org.
    [Fact]
    public async Task InvokeAsync_SubOnly_ExistingLegacyOrg_SoftResolves()
    {
        await using var db = NewDb();
        var legacy = SeedOrg("user_L", "LegacyTenant");
        db.Organisations.Add(legacy);
        await db.SaveChangesAsync();

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(new Claim("sub", "user_L"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        Assert.Equal(1, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(legacy.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }

    // 5. SUB-ONLY, no org, no legacy → fail closed, never mint a sub-keyed tenant.
    [Fact]
    public async Task InvokeAsync_SubOnly_NoOrg_FailsClosed()
    {
        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(new Claim("sub", "user_LONELY"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        Assert.Equal(0, await db.Organisations.CountAsync());
        Assert.Empty(analytics.CapturedEvents);
        Assert.False(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
    }

    // 6. NO CROSS-TENANT ADOPT: a different user's personal tenant is untouched;
    //    a fresh org is provisioned instead.
    [Fact]
    public async Task InvokeAsync_NewOrg_OtherUsersTenant_NotAdopted_ProvisionsFresh()
    {
        await using var db = NewDb();
        var other = SeedOrg("user_OTHER", "OthersWorkspace");
        db.Organisations.Add(other);
        await db.SaveChangesAsync();
        var otherId = other.Id;

        var analytics = new FakeAnalyticsService();
        var ctx = WithClaims(
            new Claim("org_id", "org_NEW2"),
            new Claim("org_slug", "me-co"),
            new Claim("sub", "user_ME"));

        await NewMiddleware().InvokeAsync(ctx, db, analytics);

        // Another user's row is UNTOUCHED.
        var otherStill = await db.Organisations.AsNoTracking()
            .SingleAsync(o => o.Id == otherId);
        Assert.Equal("user_OTHER", otherStill.ClerkOrgId);

        // A fresh org was provisioned for the org_NEW2 / user_ME login.
        Assert.Equal(2, await db.Organisations.CountAsync());
        var fresh = await db.Organisations.AsNoTracking()
            .SingleAsync(o => o.ClerkOrgId == "org_NEW2");
        var ev = Assert.Single(analytics.CapturedEvents);
        Assert.Equal("org_created", ev.EventName);
        Assert.True(ctx.Items.ContainsKey(CurrentTenantService.Items.OrganisationId));
        Assert.Equal(fresh.Id, (Guid)ctx.Items[CurrentTenantService.Items.OrganisationId]!);
    }
}
