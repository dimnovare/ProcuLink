using System.Diagnostics;
using System.Net;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  Deep /health (liveness vs readiness) regression tests.
//
//  Guards two reliability hardenings:
//   1. /health is a FAST, dependency-free liveness probe (bare 200) — Railway's
//      container probe must never be blocked by a slow/flaky dependency.
//   2. /health/ready runs the "ready"-tagged dependency checks (DB + storage +
//      migration-readiness flag) and reflects their aggregate status. A forced
//      migration failure (MigrationReadiness.MarkFailed) flips it to 503 so
//      Railway/monitoring can see the stale-schema state without the process
//      going down (liveness stays up).
//
//  These tests run SERIALLY within one class (xUnit serializes methods in a
//  class) and always restore the process-global MigrationReadiness flag, so the
//  shared static can't leak across tests.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// WebApplicationFactory with EF + Hangfire swapped to in-memory so no Postgres
/// connection is attempted. R2 keys are absent → LocalFileStorageService backs the
/// storage health check. The in-memory EF provider's CanConnectAsync returns true,
/// so the database check is healthy by default — the deterministic forced-unhealthy
/// lever in the tests is the migration-readiness flag.
/// </summary>
public sealed class HealthTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            const string testEncKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=";
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"]              = testEncKey,
                ["ConnectionStrings:DefaultConnection"] = "Host=test;Database=test",
                ["Clerk:Authority"]                     = "https://test.clerk.accounts.dev",
                ["Sentry:Dsn"]                          = "",
                ["Storage:R2AccessKeyId"]               = "",   // → LocalFileStorageService
                ["Frontend:Url"]                        = "https://proculink.eu",
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ProcuLinkDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            var contextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ProcuLinkDbContext));
            if (contextDescriptor is not null) services.Remove(contextDescriptor);

            services.AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(_dbName));

            var hangfireDescriptors = services
                .Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true)
                .ToList();
            foreach (var d in hangfireDescriptors) services.Remove(d);

            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());
        });
    }
}

public sealed class HealthEndpointTests : IClassFixture<HealthTestFactory>
{
    private readonly HealthTestFactory _factory;

    public HealthEndpointTests(HealthTestFactory factory) => _factory = factory;

    // ── Liveness: fast, dependency-free 200 ──────────────────────────────────

    [Fact]
    public async Task Liveness_Health_Returns200()
    {
        // Ensure no leaked failure state from another test affects liveness
        // (liveness must be independent of the migration flag regardless).
        MigrationReadiness.MarkSucceeded();

        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("OK", body);
    }

    [Fact]
    public async Task Liveness_Health_IsFast_AndIndependentOfDependencyState()
    {
        var client = _factory.CreateClient();

        // Even with migrations marked FAILED (a readiness dependency down), the
        // liveness probe must still return a fast 200 — it must NOT consult the
        // dependency checks.
        MigrationReadiness.MarkFailed();
        try
        {
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("/health");
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Generous ceiling — proves it isn't doing slow dependency work, while
            // staying robust on a cold CI host.
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"Liveness probe took {sw.ElapsedMilliseconds}ms — it must not depend on slow checks.");
        }
        finally
        {
            MigrationReadiness.MarkSucceeded();
        }
    }

    // ── Readiness: reflects dependency status ────────────────────────────────

    [Fact]
    public async Task Readiness_AllDependenciesHealthy_Returns200()
    {
        MigrationReadiness.MarkSucceeded();

        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        // InMemory DB CanConnect=true, Local storage signs a URL, migrations not
        // failed → aggregate Healthy.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_ForcedUnhealthyDependency_Returns503()
    {
        var client = _factory.CreateClient();

        // Force the migration-readiness dependency unhealthy (the mockable lever
        // the production fail-loud path flips after all retries are exhausted).
        MigrationReadiness.MarkFailed();
        try
        {
            var response = await client.GetAsync("/health/ready");

            // An Unhealthy "ready"-tagged check makes MapHealthChecks return 503.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            // Restore so we don't poison sibling tests sharing the process-global flag.
            MigrationReadiness.MarkSucceeded();
        }
    }

    [Fact]
    public async Task Readiness_RecoversAfterFailureCleared()
    {
        var client = _factory.CreateClient();

        MigrationReadiness.MarkFailed();
        var failed = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        MigrationReadiness.MarkSucceeded();
        var recovered = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
