using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ────────────────────────────────────────────────────────────────────────────
// Test auth handler
// Authenticates every request with the claims baked into TestAuthOptions.
// The factory controls which org/user the "logged-in" user belongs to.
// ────────────────────────────────────────────────────────────────────────────

public sealed class TestAuthOptions : AuthenticationSchemeOptions
{
    public string OrgId   { get; set; } = "org_TEST_ORG_A";
    public string OrgSlug { get; set; } = "test-org-a";
    public string Sub     { get; set; } = "user_TEST_USER_A";
}

public sealed class TestAuthHandler : AuthenticationHandler<TestAuthOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<TestAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub",      Options.Sub),
            new Claim("org_id",   Options.OrgId),
            new Claim("org_slug", Options.OrgSlug),
            // Clerk's authorized-party claim — satisfy azp validation check
            new Claim("azp", "http://localhost:3000"),
        };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Factory
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// WebApplicationFactory that:
/// - Replaces Npgsql EF Core with InMemory (unique DB per factory instance).
/// - Replaces Hangfire Postgres storage with Hangfire in-memory storage.
/// - Replaces Clerk JWT auth + ApiKey auth with a single test auth scheme
///   whose claims are fully controlled by <see cref="TestAuthOptions"/>.
/// - Provides minimal config so DeliveryEncryptionService starts without a real key.
/// </summary>
public sealed class TenancyTestFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Shared in-memory DB name. Each factory instance gets its own DB so tests
    /// cannot bleed data into each other.
    /// </summary>
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    /// <summary>Claims used by the test auth handler for every request on <see cref="DefaultClient"/>.</summary>
    public TestAuthOptions DefaultAuthOptions { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use "Testing" so StartupConfigurationValidator only warns — never throws.
        builder.UseEnvironment("Testing");

        // Inject minimal config that satisfies services which throw on absent keys.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            // A valid 32-byte base64 key. All-zero fails the prod guard; this is
            // intentionally NOT all-zero (it ends in a non-zero byte).
            const string testEncKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=";

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"]            = testEncKey,
                ["ConnectionStrings:DefaultConnection"] = "Host=test;Database=test",
                ["Clerk:Authority"]                   = "https://test.clerk.accounts.dev",
                ["Hangfire:UseInMemory"]              = "true",
                // Sentry DSN — empty disables Sentry
                ["Sentry:Dsn"]                        = "",
                // Suppress R2/Stripe warnings (optional keys in non-prod)
                ["Storage:R2AccessKeyId"]             = "",
                ["Frontend:Url"]                      = "http://localhost:3000",
            });
        });

        builder.ConfigureServices(services =>
        {
            // ── Replace Npgsql EF Core with InMemory ───────────────────
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ProcuLinkDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            // Remove the old DbContext registration too
            var contextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ProcuLinkDbContext));
            if (contextDescriptor is not null)
                services.Remove(contextDescriptor);

            services.AddDbContext<ProcuLinkDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // ── Replace Hangfire Postgres with InMemory ─────────────────
            // Remove all Hangfire storage-related registrations and re-register
            // with memory storage so no Postgres connection is attempted.
            var hangfireDescriptors = services
                .Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true)
                .ToList();
            foreach (var d in hangfireDescriptors)
                services.Remove(d);

            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());

            // ── Replace all auth schemes with single test scheme ────────
            // Remove the existing authentication configuration built by Program.cs
            // (JwtBearer + ApiKey) and install our controllable test handler.
            var authDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Authentication") == true ||
                            d.ServiceType.FullName?.Contains("JwtBearer") == true)
                .ToList();
            foreach (var d in authDescriptors)
                services.Remove(d);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme    = "Test";
            })
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", opts =>
            {
                opts.OrgId   = DefaultAuthOptions.OrgId;
                opts.OrgSlug = DefaultAuthOptions.OrgSlug;
                opts.Sub     = DefaultAuthOptions.Sub;
            });
        });
    }

    /// <summary>
    /// Creates an HttpClient with a Bearer token header set. The token value is
    /// arbitrary since our test auth handler always succeeds regardless of the
    /// token content; it just needs to look like a Bearer header so the middleware
    /// processes it as an authenticated request.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    /// <summary>
    /// Opens a scoped EF DbContext against the shared in-memory DB so tests can
    /// seed data directly without going through HTTP.
    /// </summary>
    public ProcuLinkDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Tests
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// HTTP-boundary tenancy isolation tests.
///
/// Goal: prove that a user authenticated as Org A cannot read data belonging to
/// Org B — even when Org B's order ID is known. This is the highest-value missing
/// test category: all existing tests use InMemory EF without the full HTTP
/// pipeline, so the TenantResolutionMiddleware + [Authorize] + CurrentTenantService
/// chain has never been exercised end-to-end.
/// </summary>
public sealed class TenancyIsolationTests : IDisposable
{
    private readonly TenancyTestFactory _factory;

    public TenancyIsolationTests()
    {
        _factory = new TenancyTestFactory();
    }

    public void Dispose() => _factory.Dispose();

    // ────────────────────────────────────────────────────────────────────────
    // Helper: seed an org + supplier + order stub directly into the InMemory DB
    // ────────────────────────────────────────────────────────────────────────

    private static async Task<(Organisation Org, Supplier Supplier, PurchaseOrderEntity Order)>
        SeedOrgAndOrderAsync(ProcuLinkDbContext db, string clerkOrgId, string slug)
    {
        var now = DateTime.UtcNow;

        var org = new Organisation
        {
            Id             = Guid.NewGuid(),
            ClerkOrgId     = clerkOrgId,
            Name           = $"Org-{clerkOrgId}",
            Slug           = slug,
            Plan           = "pilot",
            AccountStatus  = "trialing",
            CreatedAt      = now,
            TrialStartedAt = now,
            TrialEndsAt    = now.AddDays(14),
        };

        var supplier = new Supplier
        {
            Id           = Guid.NewGuid(),
            OrgId        = org.Id,
            Name         = $"Supplier-for-{clerkOrgId}",
            CreatedAt    = now,
        };

        var order = new PurchaseOrderEntity
        {
            Id            = Guid.NewGuid(),
            OrgId         = org.Id,
            SupplierId    = supplier.Id,
            PoNumber      = $"PO-{clerkOrgId}-001",
            OrderDate     = DateOnly.FromDateTime(now),
            Currency      = "EUR",
            Status        = "ready",
            SourceFileKey = $"{org.Id}/{Guid.NewGuid()}/file.csv",
            CreatedAt     = now,
            UpdatedAt     = now,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        db.Organisations.Add(org);
        db.Suppliers.Add(supplier);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (org, supplier, order);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 1 (PRIMARY): Cross-tenant read is blocked
    //
    // Org A authenticates. Org B's order exists in the DB. Org A must get 404
    // (order not found for their org scope), never Org B's data.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrder_AuthenticatedAsOrgA_CannotReadOrgBOrder_Returns404()
    {
        // Arrange: seed org B with an order directly into the InMemory DB.
        await using var db = _factory.CreateDbContext();

        var (orgB, _, orgBOrder) = await SeedOrgAndOrderAsync(
            db, clerkOrgId: "org_B_ISOLATION_TEST", slug: "org-b-test");

        // Also seed org A so the middleware finds it (rather than auto-provisioning
        // a new one) — this makes the test deterministic regardless of timing.
        await SeedOrgAndOrderAsync(
            db, clerkOrgId: _factory.DefaultAuthOptions.OrgId, slug: "org-a-test");

        // Act: authenticated as Org A, request Org B's order by ID.
        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/api/orders/{orgBOrder.Id}");

        // Assert: must be 404 (org-scoped lookup returns null → NotFound).
        // It must NEVER be 200 with Org B's data.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 2 (Smoke): Org A can read its own orders (200 OK)
    //
    // Proves the happy path still works — authentication and tenant resolution
    // succeed, and the org-scoped list endpoint returns 200.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListOrders_AuthenticatedAsOrgA_Returns200()
    {
        // Seed org A so middleware finds it.
        await using var db = _factory.CreateDbContext();
        await SeedOrgAndOrderAsync(
            db, clerkOrgId: _factory.DefaultAuthOptions.OrgId, slug: "org-a-smoke-test");

        var client = _factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
