using System.Diagnostics;
using System.Net;
using System.Text.Json;
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
//  ISOLATION. These tests run SERIALLY within one class (xUnit serializes methods
//  in a class) and always restore the process-global MigrationReadiness flag. That
//  is necessary and NOT sufficient, and this comment used to stop there and
//  conclude "the shared static can't leak across tests" — reasoning only about
//  methods of THIS class while the dangerous writer was a different CLASS.
//  MigrationReadiness is a static volatile int, i.e. per-PROCESS, but xUnit gives
//  every test class its own host, and classes in different collections run
//  CONCURRENTLY. Anything that boots a WebApplicationFactory<Program> also writes
//  it, because Program.cs's ApplicationStarted migration task does.
//
//  So the class joins the "postgres-container" collection — the assembly's single
//  serialisation domain for process-global state (see PostgresContainerCollection).
//  Membership is enforced by ProcessGlobalStateIsSerializedTests, not remembered.
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

[Collection("postgres-container")]
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

    // ── Tri-state: Pending → not ready ──────────────────────────────────────

    [Fact]
    public async Task Readiness_WhenPending_Returns503()
    {
        var client = _factory.CreateClient();

        // Simulate the "migration not yet complete" window that starts the moment
        // the app boots (before MigrateAsync finishes).
        MigrationReadiness.MarkPending();
        try
        {
            var response = await client.GetAsync("/health/ready");

            // Pending means the schema has NOT been proven applied yet — NOT ready.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            MigrationReadiness.MarkSucceeded();
        }
    }

    [Fact]
    public async Task Readiness_WhenPending_LivenessStillReturns200()
    {
        var client = _factory.CreateClient();

        // Pending readiness must NEVER affect the liveness probe.
        MigrationReadiness.MarkPending();
        try
        {
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            MigrationReadiness.MarkSucceeded();
        }
    }

    [Fact]
    public async Task Readiness_WhenFailed_Returns503()
    {
        var client = _factory.CreateClient();

        MigrationReadiness.MarkFailed();
        try
        {
            var response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            MigrationReadiness.MarkSucceeded();
        }
    }

    [Fact]
    public async Task Readiness_WhenSucceeded_Returns200()
    {
        var client = _factory.CreateClient();

        MigrationReadiness.MarkSucceeded();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_PendingThenSucceeded_TransitionsToReady()
    {
        var client = _factory.CreateClient();

        // Simulate full startup lifecycle: Pending → Succeeded.
        MigrationReadiness.MarkPending();
        var pendingResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pendingResponse.StatusCode);

        MigrationReadiness.MarkSucceeded();
        var succeededResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, succeededResponse.StatusCode);
    }

    // ── Structured JSON body + worker-heartbeat flattening ───────────────────

    [Fact]
    public async Task Readiness_EmitsStructuredJsonBody_WithPerCheckEntries()
    {
        MigrationReadiness.MarkSucceeded();

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        // Body is JSON (not the default plain "Healthy" string).
        Assert.Equal("application/json",
            response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Top-level contract fields.
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("ready", out var ready));
        Assert.True(root.TryGetProperty("workerHealthy", out _));
        Assert.True(root.TryGetProperty("totalDurationMs", out _));
        Assert.True(root.TryGetProperty("checks", out var checks));
        Assert.Equal(JsonValueKind.Array, checks.ValueKind);

        // Every dependency check is present and named.
        var names = checks.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToHashSet();
        Assert.Contains("database", names);
        Assert.Contains("storage", names);
        Assert.Contains("migrations", names);
        Assert.Contains("worker", names);

        // ready mirrors the HTTP contract (not Unhealthy → ready:true).
        Assert.True(ready.GetBoolean());
    }

    [Fact]
    public async Task Readiness_NoWorkerRegistered_StaysHttp200_ButWorkerHealthyFalse()
    {
        // The in-memory Hangfire storage has NO server registered (no
        // AddHangfireServer in the test host), so the worker check is Degraded.
        // Degraded must keep /health/ready at HTTP 200 (a dead Worker must not
        // evict the API), while the JSON flag reports the problem for alerting.
        MigrationReadiness.MarkSucceeded();

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.False(root.GetProperty("workerHealthy").GetBoolean());
        // Aggregate status is Degraded (worker check is the only non-Healthy one).
        Assert.Equal("Degraded", root.GetProperty("status").GetString());

        // The worker check carries machine-readable data (no secrets — counts only).
        var worker = root.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "worker");
        Assert.Equal("Degraded", worker.GetProperty("status").GetString());
        Assert.Equal(0, worker.GetProperty("data").GetProperty("activeWorkers").GetInt32());
    }

    [Fact]
    public async Task Readiness_Body_NeverLeaksSecretsOrStackTraces()
    {
        // Force an Unhealthy migration check (the path that would carry an
        // exception). The JSON writer must surface only status/description —
        // never a stack trace, connection string, or credential.
        MigrationReadiness.MarkFailed();
        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");
            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StackTrace", body);
            Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", body); // .NET stack-frame prefix
        }
        finally
        {
            MigrationReadiness.MarkSucceeded();
        }
    }
}
