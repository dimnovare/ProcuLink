using System.Net;
using System.Net.Http.Headers;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ════════════════════════════════════════════════════════════════════════════
//  Program.cs hardening regression tests (P1-2 ProblemDetails / exception
//  handler, P1-4 CORS wildcard removal, P2-4 rate-limit named policies).
//
//  These guard the three security hardenings against silent regression:
//   1. A wildcard subdomain of a configured origin must NOT be reflected as an
//      allowed credentialed CORS origin — only the EXACT configured origins are.
//   2. The expensive/abusable endpoints each have a named rate-limit policy and
//      a global backstop limiter is configured.
//   3. The RFC-7807 ProblemDetails service backing the global exception handler
//      is registered.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Factory with an EXACT two-origin Frontend:Url (mirrors prod
/// "https://proculink.eu,https://www.proculink.eu") so the CORS wildcard
/// regression test is meaningful. EF/Hangfire are swapped to in-memory so no
/// Postgres connection is attempted.
/// </summary>
public sealed class HardeningTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    public const string ExactOrigin   = "https://proculink.eu";
    public const string ExactWwwOrigin = "https://www.proculink.eu";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            const string testEncKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=";
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"]               = testEncKey,
                ["ConnectionStrings:DefaultConnection"]  = "Host=test;Database=test",
                ["Clerk:Authority"]                      = "https://test.clerk.accounts.dev",
                ["Sentry:Dsn"]                           = "",
                ["Storage:R2AccessKeyId"]                = "",
                // Exact prod-shaped origin list — NO wildcard.
                ["Frontend:Url"]                         = $"{ExactOrigin},{ExactWwwOrigin}",
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

public sealed class ProgramHardeningTests : IClassFixture<HardeningTestFactory>
{
    private readonly HardeningTestFactory _factory;

    public ProgramHardeningTests(HardeningTestFactory factory) => _factory = factory;

    // ────────────────────────────────────────────────────────────────────────
    // P1-4: CORS must NOT allow a wildcard subdomain of a configured origin.
    //
    // With the (removed) SetIsOriginAllowedToAllowWildcardSubdomains() a request
    // from https://evil.proculink.eu would be reflected back as an allowed
    // credentialed origin. After the fix only the EXACT configured origins are
    // reflected; a sibling subdomain gets NO Access-Control-Allow-Origin header.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cors_ExactConfiguredOrigin_IsReflected()
    {
        var client = _factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Options, "/api/orders");
        req.Headers.Add("Origin", HardeningTestFactory.ExactOrigin);
        req.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(req);

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values),
            "Exact configured origin must be reflected by CORS.");
        Assert.Equal(HardeningTestFactory.ExactOrigin, Assert.Single(values));
    }

    [Fact]
    public async Task Cors_WildcardSubdomainOfConfiguredOrigin_IsNotAllowed()
    {
        var client = _factory.CreateClient();

        // A sibling subdomain of the configured apex — would have been allowed by
        // the removed wildcard-subdomain matcher. Must now be rejected.
        const string siblingSubdomain = "https://evil.proculink.eu";

        var req = new HttpRequestMessage(HttpMethod.Options, "/api/orders");
        req.Headers.Add("Origin", siblingSubdomain);
        req.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(req);

        // No Access-Control-Allow-Origin header means the origin is not allowed.
        var allowed = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
            ? values.FirstOrDefault()
            : null;

        Assert.NotEqual(siblingSubdomain, allowed);
        Assert.Null(allowed);
    }

    // ────────────────────────────────────────────────────────────────────────
    // P2-4: every expensive/abusable endpoint class has a named rate-limit
    // policy, the original "upload" policy survives, and a global limiter exists.
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("upload")]
    [InlineData("transform")]
    [InlineData("ai")]
    [InlineData("signed-url")]
    [InlineData("webhook")]
    public void RateLimiter_NamedPolicy_IsRegistered(string policyName)
    {
        var options = _factory.Services
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        // The partition-delegate AddPolicy overload registers into the internal
        // PolicyMap (DI/IRateLimiterPolicy overloads use UnactivatedPolicyMap).
        // Check both maps so the assertion is robust regardless of overload.
        Assert.True(
            PolicyIsRegistered(options, policyName),
            $"Rate-limit policy '{policyName}' must be registered.");
    }

    [Fact]
    public void RateLimiter_GlobalLimiter_IsConfigured()
    {
        var options = _factory.Services
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.NotNull(options.GlobalLimiter);
    }

    [Fact]
    public void RateLimiter_RejectionStatus_Is429()
    {
        var options = _factory.Services
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
    }

    // ────────────────────────────────────────────────────────────────────────
    // P1-2: the RFC-7807 ProblemDetails service backing UseExceptionHandler()
    // is registered (AddProblemDetails()).
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProblemDetails_Service_IsRegistered()
    {
        var svc = _factory.Services.GetService<IProblemDetailsService>();
        Assert.NotNull(svc);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Baseline security response headers (HSTS + nosniff + Referrer-Policy) are
    // emitted on every response via the OnStarting middleware. Runs before
    // routing, so even a non-200 carries them.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecurityHeaders_ArePresentOnEveryResponse()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.True(
            response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff)
                && nosniff.Contains("nosniff"),
            "X-Content-Type-Options: nosniff must be present on every response.");

        Assert.True(
            response.Headers.TryGetValues("Strict-Transport-Security", out var hsts)
                && hsts.Any(v => v.Contains("max-age=")),
            "Strict-Transport-Security must be present on every response.");

        Assert.True(
            response.Headers.Contains("Referrer-Policy"),
            "Referrer-Policy must be present on every response.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="policyName"/> is registered in either of
    /// RateLimiterOptions' internal policy maps (PolicyMap for partition-delegate
    /// policies, UnactivatedPolicyMap for DI policies). Reflection is required
    /// because both maps are internal; checking both keeps the assertion robust
    /// across overloads and minor framework versions.
    /// </summary>
    private static bool PolicyIsRegistered(RateLimiterOptions options, string policyName)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public;

        foreach (var mapName in new[] { "PolicyMap", "UnactivatedPolicyMap" })
        {
            var prop = typeof(RateLimiterOptions).GetProperty(mapName, flags);
            if (prop?.GetValue(options) is System.Collections.IDictionary map &&
                map.Contains(policyName))
            {
                return true;
            }
        }
        return false;
    }
}
