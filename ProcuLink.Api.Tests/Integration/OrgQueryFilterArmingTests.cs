using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Records what the REQUEST-scoped <see cref="ProcuLinkDbContext"/> actually looked like at the
/// moment the controller action ran. One instance per factory, so tests never share it.
/// </summary>
public sealed class OrgScopeProbe
{
    public bool Observed { get; set; }
    public Guid? ScopedOrganisationId { get; set; }
    public string? CrossOrganisationReason { get; set; }

    /// <summary>
    /// Distinct organisation ids returned by a deliberately UNPREDICATED query — no
    /// <c>.Where(o =&gt; o.OrgId == …)</c> — on the request's own context. This is the defect verbatim:
    /// the question is what a query that forgot the predicate can see.
    /// </summary>
    public List<Guid> OrgIdsVisibleToAnUnpredicatedQuery { get; } = [];
}

/// <summary>
/// Global action filter that fills an <see cref="OrgScopeProbe"/> from the request's own DI scope.
///
/// <para>It reads the context the CONTROLLER would use, resolved from
/// <c>HttpContext.RequestServices</c> — not a context the test constructed — so it cannot pass by
/// testing something adjacent to the real thing.</para>
/// </summary>
public sealed class OrgScopeProbeFilter(OrgScopeProbe probe) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<ProcuLinkDbContext>();

        probe.ScopedOrganisationId = db.ScopedOrganisationId;
        probe.CrossOrganisationReason = db.CrossOrganisationReason;

        probe.OrgIdsVisibleToAnUnpredicatedQuery.Clear();
        probe.OrgIdsVisibleToAnUnpredicatedQuery.AddRange(
            await db.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).Distinct().ToListAsync());

        probe.Observed = true;

        await next();
    }
}

/// <summary>
/// WebApplicationFactory for arming tests. Same substitutions as <see cref="TenancyTestFactory"/>
/// (InMemory EF, InMemory Hangfire, one controllable auth scheme), plus a configured platform-admin
/// allowlist and the <see cref="OrgScopeProbeFilter"/>.
/// </summary>
public sealed class OrgScopeArmingFactory : WebApplicationFactory<Program>
{
    public const string AdminSub = "user_ARMING_ADMIN";

    private readonly string _dbName = Guid.NewGuid().ToString("N");

    public TestAuthOptions DefaultAuthOptions { get; } = new()
    {
        OrgId = "org_ARMING_A",
        OrgSlug = "org-arming-a",
        Sub = AdminSub,
    };

    public OrgScopeProbe Probe { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=",
                ["ConnectionStrings:DefaultConnection"] = "Host=test;Database=test",
                ["Clerk:Authority"] = "https://test.clerk.accounts.dev",
                ["Hangfire:UseInMemory"] = "true",
                ["Sentry:Dsn"] = "",
                ["Storage:R2AccessKeyId"] = "",
                ["Frontend:Url"] = "http://localhost:3000",
                // The admin surface fails closed on an empty allowlist; admit our test principal.
                ["Admin:UserIds"] = AdminSub,
            });
        });

        builder.ConfigureServices(services =>
        {
            var optionsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ProcuLinkDbContext>));
            if (optionsDescriptor is not null) services.Remove(optionsDescriptor);

            var contextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ProcuLinkDbContext));
            if (contextDescriptor is not null) services.Remove(contextDescriptor);

            services.AddDbContext<ProcuLinkDbContext>(options => options.UseInMemoryDatabase(_dbName));

            var hangfireDescriptors = services
                .Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true)
                .ToList();
            foreach (var d in hangfireDescriptors) services.Remove(d);

            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());

            var authDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Authentication") == true ||
                            d.ServiceType.FullName?.Contains("JwtBearer") == true)
                .ToList();
            foreach (var d in authDescriptors) services.Remove(d);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            })
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", opts =>
            {
                opts.OrgId = DefaultAuthOptions.OrgId;
                opts.OrgSlug = DefaultAuthOptions.OrgSlug;
                opts.Sub = DefaultAuthOptions.Sub;
            });

            services.AddSingleton(Probe);
            services.AddScoped<OrgScopeProbeFilter>();
            services.Configure<MvcOptions>(o =>
                o.Filters.Add(new ServiceFilterAttribute(typeof(OrgScopeProbeFilter))));
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    /// <summary>A context on the shared in-memory database, deliberately unscoped, for seeding.</summary>
    public ProcuLinkDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
}

/// <summary>
/// The defect, in BOTH directions, over the real HTTP pipeline — real routing, real
/// <c>TenantResolutionMiddleware</c>, real DI scoping, real controllers.
///
/// <para><b>Direction 1.</b> A request-scoped context must not return another organisation's rows,
/// even when the query forgets the predicate.</para>
///
/// <para><b>Direction 2.</b> An endpoint that opted out must still see across organisations. This is
/// the half that fails silently in production: arming truncates the platform-owner surface to the
/// signed-in admin's own tenant and returns 200 with a smaller, entirely plausible number. So it is
/// asserted on a real cross-organisation expectation — org B's rows appearing in org A's admin
/// response — not merely on the context's scope flag.</para>
///
/// <para>Two real organisations are always seeded. With one, "returns nothing" and "returns only
/// mine" are indistinguishable and every assertion here would pass against a broken filter.</para>
/// </summary>
/// <remarks>
/// In the "postgres-container" collection because it boots a <c>WebApplicationFactory&lt;Program&gt;</c>,
/// and Program.cs's migration task writes the process-global MigrationReadiness flag that other
/// classes assert on. Required by <c>ProcessGlobalStateIsSerializedTests</c>; the factory itself is
/// pure InMemory and starts no container.
/// </remarks>
[Collection("postgres-container")]
public sealed class OrgQueryFilterArmingTests : IDisposable
{
    private readonly OrgScopeArmingFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<Organisation> SeedOrgWithAnOrderAsync(
        ProcuLinkDbContext db, string clerkOrgId, string slug)
    {
        var now = DateTime.UtcNow;

        var org = new Organisation
        {
            Id = Guid.NewGuid(),
            ClerkOrgId = clerkOrgId,
            Name = $"Org-{clerkOrgId}",
            Slug = slug,
            Plan = "pilot",
            AccountStatus = "trialing",
            CreatedAt = now,
            TrialStartedAt = now,
            TrialEndsAt = now.AddDays(14),
        };

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = $"Supplier-for-{clerkOrgId}",
            CreatedAt = now,
        };

        db.Organisations.Add(org);
        db.Suppliers.Add(supplier);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            SupplierId = supplier.Id,
            PoNumber = $"PO-{clerkOrgId}-001",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = "ready",
            SourceFileKey = $"{org.Id}/{Guid.NewGuid()}/file.csv",
            CreatedAt = now,
            UpdatedAt = now,
            Lines = [],
            OutboundArtifacts = [],
        });

        await db.SaveChangesAsync();
        return org;
    }

    /// <summary>Seeds the caller's organisation and a second one, and returns both.</summary>
    private async Task<(Organisation Mine, Organisation Other)> SeedTwoOrganisationsAsync()
    {
        await using var db = _factory.CreateDbContext();

        var mine = await SeedOrgWithAnOrderAsync(db, _factory.DefaultAuthOptions.OrgId, "org-arming-a");
        var other = await SeedOrgWithAnOrderAsync(db, "org_ARMING_B", "org-arming-b");

        // Anti-vacuity: the rest of this file is meaningless unless a second organisation's rows
        // really are present to be leaked. Asserted on an unscoped context, so the filter cannot
        // hide the very rows this fixture exists to provide.
        await using var check = _factory.CreateDbContext();
        Assert.Null(check.ScopedOrganisationId);
        var seededOrgIds = await check.PurchaseOrders.AsNoTracking()
            .Select(o => o.OrgId).Distinct().ToListAsync();
        Assert.Equal(2, seededOrgIds.Count);
        Assert.Contains(mine.Id, seededOrgIds);
        Assert.Contains(other.Id, seededOrgIds);

        return (mine, other);
    }

    // ── Direction 1: an ordinary request cannot see another organisation ─────

    /// <summary>
    /// The defect verbatim, through the real pipeline: on an ordinary org-scoped endpoint, a query
    /// written WITHOUT an organisation predicate must not return another organisation's rows.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryRequest_IsArmed_AndAnUnpredicatedQueryCannotSeeAnotherOrganisation()
    {
        var (mine, other) = await SeedTwoOrganisationsAsync();

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var probe = _factory.Probe;
        Assert.True(probe.Observed, "the probe filter never ran, so this test asserted nothing");

        // The leak itself is asserted FIRST, so a failure here names the defect — another
        // organisation's rows came back — rather than reporting an unset flag and leaving the
        // reader to infer what that would have meant.
        Assert.DoesNotContain(other.Id, probe.OrgIdsVisibleToAnUnpredicatedQuery);
        Assert.Equal([mine.Id], probe.OrgIdsVisibleToAnUnpredicatedQuery);

        Assert.Equal(mine.Id, probe.ScopedOrganisationId);
    }

    // ── Direction 2: an opted-out endpoint still sees every organisation ─────

    /// <summary>
    /// The silent half. <c>AdminController</c> is the platform-owner surface and is declared
    /// <c>[CrossOrganisationRead]</c>. If arming reached it, <c>GET /api/admin/organisations</c>
    /// would still answer 200 — with one organisation in the list and the other's order volume
    /// gone. So the assertion is on the RESPONSE: org B must be listed, with the order it was
    /// seeded with.
    /// </summary>
    [Fact]
    public async Task TheAdminSurface_StillSeesEveryOrganisation()
    {
        var (mine, other) = await SeedTwoOrganisationsAsync();

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/admin/organisations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var listed = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(mine.Id, listed);
        Assert.Contains(other.Id, listed);

        // The per-organisation aggregate is the read that truncates. Org B's order must be counted
        // while the caller is signed in as org A — a number that arming would quietly turn into 0.
        var otherRow = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == other.Id);
        Assert.True(
            otherRow.GetProperty("orderVolume30d").GetInt32() >= 1,
            "another organisation's 30-day order volume read as 0 on the cross-tenant admin surface — " +
            "the request was armed and the aggregate truncated to the caller's own organisation");

        // And the context really was left unscoped, with the reason recorded.
        var probe = _factory.Probe;
        Assert.True(probe.Observed, "the probe filter never ran, so this test asserted nothing");
        Assert.Null(probe.ScopedOrganisationId);
        Assert.False(string.IsNullOrWhiteSpace(probe.CrossOrganisationReason));
        Assert.Contains(mine.Id, probe.OrgIdsVisibleToAnUnpredicatedQuery);
        Assert.Contains(other.Id, probe.OrgIdsVisibleToAnUnpredicatedQuery);
    }

    /// <summary>
    /// Arming happens after authentication and tenant resolution, both of which query the database
    /// before any organisation is known. Those bootstrap reads run on their own short-lived
    /// contexts precisely so the request context stays armable; if either regressed to sharing the
    /// request context, <c>ScopeToOrganisation</c> would throw and every authenticated request would
    /// 500. A 200 here is that regression's alarm.
    /// </summary>
    [Fact]
    public async Task TenantResolution_DoesNotConsumeTheRequestContextItLaterArms()
    {
        await SeedTwoOrganisationsAsync();

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_factory.Probe.ScopedOrganisationId);
    }

    /// <summary>
    /// An organisation Clerk has never sent us before is auto-provisioned mid-request. That write
    /// happens on the bootstrap context; the request context must still end up armed to the newly
    /// created organisation rather than left unscoped — an unscoped request would read every
    /// tenant's rows.
    /// </summary>
    [Fact]
    public async Task AFirstLoginThatProvisionsAnOrganisation_StillArmsTheRequest()
    {
        // Seed only the OTHER organisation. The caller's org does not exist yet, so tenant
        // resolution provisions it during the request.
        await using (var db = _factory.CreateDbContext())
            await SeedOrgWithAnOrderAsync(db, "org_ARMING_B", "org-arming-b");

        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var check = _factory.CreateDbContext();
        var provisioned = await check.Organisations.AsNoTracking()
            .SingleAsync(o => o.ClerkOrgId == _factory.DefaultAuthOptions.OrgId);

        Assert.Empty(_factory.Probe.OrgIdsVisibleToAnUnpredicatedQuery);
        Assert.Equal(provisioned.Id, _factory.Probe.ScopedOrganisationId);
    }
}
