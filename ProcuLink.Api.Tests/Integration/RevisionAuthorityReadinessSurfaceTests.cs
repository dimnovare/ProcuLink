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
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-21 (c) — the effective value of <c>Connections:RevisionAuthority</c> must be READABLE from
/// the running process.
///
/// <para><b>Why.</b> On 2026-07-27 an audit declared "revision authority is off in production, the
/// whole versioning subsystem is inert" as a P0. It was wrong — the flag is <c>true</c> on both
/// Railway services — but nobody could tell without shelling into Railway, because the deployed
/// value was written down nowhere and served nowhere. A configuration fact that governs whether
/// every reproducibility claim is true must not be a fact only a CLI can answer.</para>
///
/// <para><b>The surface.</b> <c>GET /health/ready</c> carries a top-level <c>revisionAuthority</c>
/// boolean — flattened like <c>workerHealthy</c> so monitoring can
/// <c>jq -e '.revisionAuthority'</c> — plus a "revisionAuthority" check entry whose data names the
/// deployed services that must carry the flag. It is a boolean, never a secret: no key, no
/// connection string, no credential. Safe on an unauthenticated probe, exactly like the checks
/// already there.</para>
///
/// <para><b>Rule R1 — a new surface needs a consumer.</b> These tests ARE the consumer, and they
/// read it through real HTTP against the real readiness pipeline, not through the writer in
/// isolation. Both polarities are asserted so the surface cannot be a constant.</para>
/// </summary>
public sealed class RevisionAuthorityReadinessSurfaceTests
{
    /// <summary>
    /// Readiness factory with EF + Hangfire in memory (no Postgres dialled) and the revision
    /// authority flag set to whatever this instance was constructed with — or omitted entirely,
    /// which is the code default and the shape production had before the Railway variable existed.
    /// </summary>
    private sealed class FlagFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString("N");
        private readonly string? _flagValue;

        public FlagFactory(string? flagValue) => _flagValue = flagValue;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Delivery:EncryptionKey"]              = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=",
                    ["ConnectionStrings:DefaultConnection"] = "Host=test;Database=test",
                    ["Clerk:Authority"]                     = "https://test.clerk.accounts.dev",
                    ["Sentry:Dsn"]                          = "",
                    ["Storage:R2AccessKeyId"]               = "",
                    ["Frontend:Url"]                        = "https://proculink.eu",
                };
                if (_flagValue is not null)
                    settings[EffectiveConnectionConfigResolver.FlagKey] = _flagValue;

                config.AddInMemoryCollection(settings);
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

    private static async Task<JsonElement> ReadinessBodyAsync(string? flagValue)
    {
        MigrationReadiness.MarkSucceeded();
        await using var factory = new FlagFactory(flagValue);
        var response = await factory.CreateClient().GetAsync("/health/ready");

        // A Degraded worker/storage keeps 200; only Unhealthy is 503. Either way the body parses.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Production's shape: the Railway variable sets it true → the probe says true.</summary>
    [Fact]
    public async Task ReadinessBody_ReportsRevisionAuthorityTrue_WhenTheFlagIsOn()
    {
        var body = await ReadinessBodyAsync("true");

        Assert.True(
            body.TryGetProperty("revisionAuthority", out var flag),
            "GET /health/ready must carry a top-level `revisionAuthority` boolean — the deployed "
            + "value of Connections:RevisionAuthority has to be readable without shelling into Railway.");
        Assert.Equal(JsonValueKind.True, flag.ValueKind);
    }

    /// <summary>
    /// The other polarity — and the shape the audit ASSUMED production had. Omitting the key
    /// entirely (not merely setting it false) is the code default, so this is the exact
    /// configuration whose invisibility caused the false P0.
    /// </summary>
    [Fact]
    public async Task ReadinessBody_ReportsRevisionAuthorityFalse_WhenTheKeyIsAbsent()
    {
        var body = await ReadinessBodyAsync(flagValue: null);

        Assert.True(
            body.TryGetProperty("revisionAuthority", out var flag),
            "GET /health/ready must carry `revisionAuthority` even when the key is unset — "
            + "absent must read as an explicit false, never as a missing field.");
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    /// <summary>
    /// The flag is also its own readiness CHECK, so the value travels with a human-readable
    /// description and the list of deployed services that must carry it — the check body is where
    /// an operator learns what to fix, not just what is wrong.
    /// </summary>
    [Fact]
    public async Task ReadinessChecks_IncludeARevisionAuthorityEntry_NamingTheHostsThatMustCarryTheFlag()
    {
        var body = await ReadinessBodyAsync("true");

        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        var entry = checks.SingleOrDefault(c =>
            c.GetProperty("name").GetString() == "revisionAuthority");

        Assert.True(entry.ValueKind == JsonValueKind.Object,
            "readiness must expose a 'revisionAuthority' check; found: "
            + string.Join(", ", checks.Select(c => c.GetProperty("name").GetString())));

        // Never Unhealthy: the flag's value is information, not a reason to evict the process.
        Assert.Equal("Healthy", entry.GetProperty("status").GetString());

        var data = entry.GetProperty("data");

        // Rule R5 — the check's own comment says "SECURITY: no secret value is read or emitted, so
        // this is safe on the unauthenticated probe". /health/ready is anonymous, so that sentence
        // is a proof obligation, and asserting the four expected keys are PRESENT does not
        // discharge it: adding ["connectionString"] = cfg["ConnectionStrings:DefaultConnection"]
        // would keep such a test green while publishing the database password to the internet.
        // Pin the key set EXACTLY, so any new field has to come past this assertion.
        Assert.Equal(
            new[] { "configurationKey", "enabled", "environmentVariable", "hostsRequiringTheFlag" },
            data.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(JsonValueKind.True, data.GetProperty("enabled").ValueKind);
        Assert.Equal(
            EffectiveConnectionConfigResolver.FlagKey,
            data.GetProperty("configurationKey").GetString());
        Assert.Equal(
            EffectiveConnectionConfigResolver.EnvironmentVariableName,
            data.GetProperty("environmentVariable").GetString());

        // Every host that resolves an effective config must set the flag — the check says which.
        var hosts = data.GetProperty("hostsRequiringTheFlag").EnumerateArray()
            .Select(h => h.GetString()).ToList();
        Assert.Equal(RevisionAuthorityHosts.All.Select(h => h.DeployedServiceName).OrderBy(x => x, StringComparer.Ordinal),
                     hosts.OrderBy(x => x, StringComparer.Ordinal));
    }
}
