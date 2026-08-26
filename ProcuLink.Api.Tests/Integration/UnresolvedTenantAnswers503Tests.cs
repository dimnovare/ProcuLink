using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
// Test auth handler — the claim SHAPE is chosen per request
//
// The three cases below differ only in which of two claims the token carries, so
// they are driven by request headers rather than by three hosts. Absent header =
// absent claim, which is the whole point: this suite is about what happens when a
// claim ISN'T there.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Authenticates every request. <c>sub</c> and <c>org_id</c> are each emitted only when the
/// matching header is present, so a test can produce a token that authenticates a real user who
/// belongs to no organisation — the production shape this suite exists for.
/// </summary>
public sealed class TenantClaimShapeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TenantClaimShapeTest";

    /// <summary>The Clerk user id to present, or absent to present NO sub claim at all.</summary>
    public const string SubHeader = "X-Test-Sub";

    /// <summary>The Clerk organisation id to present, or absent to present none.</summary>
    public const string OrgHeader = "X-Test-Org";

    public TenantClaimShapeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Clerk's authorized-party claim — satisfies the azp validation in Program.cs. Always
        // present, so the identity is authenticated even when both claims below are absent.
        var claims = new List<Claim> { new("azp", "http://localhost:3000") };

        if (Header(SubHeader) is { } sub) claims.Add(new Claim("sub", sub));
        if (Header(OrgHeader) is { } org) claims.Add(new Claim("org_id", org));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    private string? Header(string name) =>
        Request.Headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v.ToString())
            ? v.ToString()
            : null;
}

/// <summary>
/// Boots the real API — real middleware order, real routing, real
/// <c>UseExceptionHandler()</c> chain — against EF InMemory and Hangfire InMemory, with
/// authentication replaced by <see cref="TenantClaimShapeAuthHandler"/>.
///
/// <para>The exception handler is the thing under test, so it must be the REAL one from
/// Program.cs. Nothing here touches it.</para>
/// </summary>
public sealed class TenantClaimShapeFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString("N");

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
            });
        });

        builder.ConfigureServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<ProcuLinkDbContext>)
                                               || d.ServiceType == typeof(ProcuLinkDbContext)).ToList())
                services.Remove(d);

            services.AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(_dbName));

            foreach (var d in services.Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true).ToList())
                services.Remove(d);

            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());

            foreach (var d in services.Where(d => d.ServiceType.FullName?.Contains("Authentication") == true
                                               || d.ServiceType.FullName?.Contains("JwtBearer") == true).ToList())
                services.Remove(d);

            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = TenantClaimShapeAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = TenantClaimShapeAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TenantClaimShapeAuthHandler>(
                    TenantClaimShapeAuthHandler.SchemeName, _ => { });
        });
    }

    public ProcuLinkDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
}

// ────────────────────────────────────────────────────────────────────────────
// Tests
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A request whose tenant cannot be resolved is not a server fault.
///
/// <para><b>The defect.</b> Every tenant-scoped endpoint reads
/// <c>ICurrentTenantService.OrganisationId</c>. When no organisation is resolved that property
/// throws, nothing mapped the throw, and <c>UseExceptionHandler()</c> gave it the answer it gives
/// any unrecognised exception: <b>HTTP 500</b>. In production it fires on a brand-new
/// organisation's FIRST page load — Clerk mints the session token before the organisation claim is
/// attached — and was measured on every scheduled smoke run for over a week. Three frontend fixes
/// took it from four-to-five 500s per run down to one, and could not close the last one: the
/// client cannot tell "this user has no Clerk organisation" from "the organisation exists but is
/// not visible yet", because Clerk's membership list is eventually consistent. The server does not
/// need to tell them apart — it knows it has no tenant right now, which is all 503 claims.</para>
///
/// <para><b>Why these three tests and not one.</b> The first proves the mapping. The second is the
/// one that matters: <c>ICurrentTenantService.ClerkUserId</c> throws
/// <see cref="UnauthorizedAccessException"/> for the genuinely different condition "no sub claim —
/// there is no authenticated user at all", and it must keep failing exactly as it does today. A
/// mapping written against the exception TYPE the two used to share would have swallowed it. The
/// third is the floor: a legacy sub-keyed tenant — the shape the entire pre-organisations customer
/// base still has — must keep resolving and answering 200, so nobody can satisfy the first test by
/// making 503 the answer to everything.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class UnresolvedTenantAnswers503Tests : IClassFixture<TenantClaimShapeFactory>
{
    private readonly TenantClaimShapeFactory _factory;

    public UnresolvedTenantAnswers503Tests(TenantClaimShapeFactory factory) => _factory = factory;

    /// <summary>
    /// The Retry-After the client actually receives. Written as a literal on purpose: it is a
    /// value real callers back off by, so a change to it should turn this red and be decided,
    /// not inherited silently from whichever constant the handler happens to read.
    /// (Today: <c>TenantResolutionMiddleware.RetryAfterSeconds</c>, shared with the 503 that
    /// middleware answers when tenant resolution cannot reach the database.)
    /// </summary>
    private const string ExpectedRetryAfter = "2";

    private HttpClient ClientWith(string? sub, string? org)
    {
        var client = _factory.CreateClient();
        if (sub is not null) client.DefaultRequestHeaders.Add(TenantClaimShapeAuthHandler.SubHeader, sub);
        if (org is not null) client.DefaultRequestHeaders.Add(TenantClaimShapeAuthHandler.OrgHeader, org);
        return client;
    }

    private async Task SeedOrganisationAsync(string clerkKey, string slug)
    {
        await using var db = _factory.CreateDbContext();

        // Already seeded by a sibling test on this shared host — nothing to do.
        if (await db.Organisations.AnyAsync(o => o.ClerkOrgId == clerkKey)) return;

        var now = DateTime.UtcNow;
        db.Organisations.Add(new Organisation
        {
            Id = Guid.NewGuid(),
            ClerkOrgId = clerkKey,
            Name = $"Org-{clerkKey}",
            Slug = slug,
            Plan = "pilot",
            AccountStatus = "trialing",
            CreatedAt = now,
            TrialStartedAt = now,
            TrialEndsAt = now.AddDays(14),
        });
        await db.SaveChangesAsync();
    }

    // ── 1. The mapping ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An authenticated user whose token carries no organisation, with no row to resolve — the
    /// brand-new-workspace shape — asks for the dashboard. Before the fix this was a 500.
    /// </summary>
    [Fact]
    public async Task NoOrganisationResolved_TheDashboardAnswers503WithRetryAfter_NotA500()
    {
        // No org claim, and no legacy row keyed to this sub, so tenant resolution leaves the
        // request unresolved and the controller's first read of OrganisationId throws.
        var client = ClientWith(sub: "user_TENANT503_BRAND_NEW", org: null);

        var response = await client.GetAsync("/api/dashboard/stats");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        // Retryable, and it says by how much. Without the header a client has nothing to act on
        // and 503 is only a nicer-looking failure.
        Assert.True(
            response.Headers.TryGetValues("Retry-After", out var retryAfter),
            "A 503 that does not say when to retry gives the caller nothing to act on.");
        Assert.Equal(ExpectedRetryAfter, Assert.Single(retryAfter!));

        // Same body shape as every other error on this API — RFC-7807 — and no internals in it.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(503, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "https://tools.ietf.org/html/rfc9110#section-15.6.4",
            body.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));

        // Program.cs's CustomizeProblemDetails still runs on this path, so the 503 is as traceable
        // in support as every other error — losing it would make the friendlier answer harder to
        // diagnose than the 500 it replaced.
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));

        // The Development-only diagnostics stay Development-only.
        Assert.False(
            body.RootElement.TryGetProperty("stackTrace", out _),
            "The 503 must not carry a stack trace outside Development.");
        Assert.False(
            body.RootElement.TryGetProperty("exception", out _),
            "The 503 must not name an internal exception type outside Development.");
    }

    // ── 2. The negative control: the sibling exception is untouched ──────────────────────────────

    /// <summary>
    /// The organisation resolves, but the token carries no <c>sub</c>, so
    /// <c>ICurrentTenantService.ClerkUserId</c> throws its own
    /// <see cref="UnauthorizedAccessException"/>. That is a different condition — no authenticated
    /// user at all, which no amount of retrying fixes — and it must still surface the way it does
    /// today, as an unmapped exception. If this ever turns 503 the mapping has drifted off the
    /// exception it was written for and is now hiding its sibling behind a retry banner.
    /// </summary>
    [Fact]
    public async Task NoSubClaim_StillFailsTheWayItAlwaysHas_AndIsNotDressedUpAsRetryable()
    {
        const string clerkOrgId = "org_TENANT503_HAS_ORG_NO_SUB";
        await SeedOrganisationAsync(clerkOrgId, "tenant503-has-org-no-sub");

        var client = ClientWith(sub: null, org: clerkOrgId);

        // POST /api/onboarding/sample-order reads OrganisationId (resolves fine) and then
        // ClerkUserId (throws) while evaluating its arguments, so nothing downstream runs.
        var response = await client.PostAsync(
            "/api/onboarding/sample-order",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Retry-After"),
            "Nothing about a missing sub claim gets better by retrying, so nothing may invite one.");
    }

    // ── 3. The floor: a resolvable tenant is still served ────────────────────────────────────────

    /// <summary>
    /// The legacy sub-keyed shape — an organisation whose <c>clerk_org_id</c> IS the user's own
    /// Clerk user id, which is what the entire pre-organisations customer base looks like. It
    /// carries no org claim either, so it is indistinguishable from test 1 at the claim level and
    /// distinguishable only by the row existing. It must still be served, or "answer 503 when the
    /// tenant is unresolved" has quietly become "answer 503 whenever there is no org claim".
    /// </summary>
    [Fact]
    public async Task ALegacySubKeyedTenant_StillResolves_AndIsServedNormally()
    {
        const string legacySub = "user_TENANT503_LEGACY";
        await SeedOrganisationAsync(legacySub, "tenant503-legacy");

        var client = ClientWith(sub: legacySub, org: null);

        var response = await client.GetAsync("/api/dashboard/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }
}
