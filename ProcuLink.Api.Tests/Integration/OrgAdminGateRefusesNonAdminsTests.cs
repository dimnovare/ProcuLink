using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Auth;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

// ────────────────────────────────────────────────────────────────────────────
// Test auth handler — the role and the org shape are chosen PER REQUEST
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Authenticates every request, with the claim set driven by request headers so one host can serve
/// the admin case, the member case, the no-role case and the legacy sub-only case without rebuilding.
/// </summary>
public sealed class RbacTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "RbacTest";

    /// <summary><c>org:admin</c>, <c>org:member</c>, or absent to emit NO role claim at all.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>The Clerk organisation id to present, or absent to present none (legacy sub-only login).</summary>
    public const string OrgHeader = "X-Test-Org";

    /// <summary>The Clerk user id to present.</summary>
    public const string SubHeader = "X-Test-Sub";

    public RbacTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new("sub", Header(SubHeader) ?? OrgAdminGateRefusesNonAdminsTests.AdminSub),
            // Clerk's authorized-party claim — satisfies the azp validation in Program.cs.
            new("azp", "http://localhost:3000"),
        };

        if (Header(OrgHeader) is { } org) claims.Add(new Claim("org_id", org));
        if (Header(RoleHeader) is { } role) claims.Add(new Claim("org_role", role));

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
/// Boots the real API — real routing, real middleware order, real filters — against EF InMemory and
/// Hangfire InMemory, with authentication replaced by <see cref="RbacTestAuthHandler"/>.
/// </summary>
public sealed class RbacTestFactory : WebApplicationFactory<Program>
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
                // Without this, POST /api/api-keys throws before it can answer, and the admin case
                // would be proved by a 500 rather than by the action's own 400. Both are "not 403",
                // but only one of them is evidence the endpoint actually works for an admin.
                ["Security:ApiKeyHashSecret"] = "test-api-key-hash-secret-not-a-real-one",
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
                    o.DefaultAuthenticateScheme = RbacTestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = RbacTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, RbacTestAuthHandler>(RbacTestAuthHandler.SchemeName, _ => { });
        });
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Tests
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The behavioural half of the org-admin gate: over real HTTP, through the real middleware and
/// filter pipeline, <b>a non-admin member is refused on every gated endpoint and an administrator is
/// admitted</b>.
///
/// <para><b>The endpoint list is not written here.</b> It is read out of the running application's
/// own <see cref="EndpointDataSource"/>, filtered to the endpoints whose metadata carries
/// <see cref="RequireOrgAdminAttribute"/>. A hard-coded list of endpoint names is how this repo's
/// drifts have survived, and it would have exactly the wrong failure mode: endpoint thirteen would
/// ship ungated and every test here would stay green. Because the list is derived from the gate
/// registration, a new gated endpoint is covered the moment it is written, and one that loses its
/// gate disappears from the list — which is what <see cref="OrgAdminGateIsRealTests"/> catches from
/// the other side, by comparing that same computed set against the IL of what the actions do.</para>
///
/// <para><b>Anti-vacuity.</b> The discovered set is asserted non-empty and of the expected size
/// before it is used, so a filter that silently matched nothing cannot make a loop over it pass.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class OrgAdminGateRefusesNonAdminsTests : IClassFixture<RbacTestFactory>
{
    /// <summary>Must match <c>OrgAdminGateIsRealTests.ExpectedGatedEndpointCount</c>.</summary>
    private const int ExpectedGatedEndpointCount = 14;

    /// <summary>An organisation with a REAL Clerk organisation id — the modern shape.</summary>
    private const string ClerkOrg = "org_TEST_RBAC";
    internal const string AdminSub = "user_TEST_RBAC_ADMIN";
    private const string MemberSub = "user_TEST_RBAC_MEMBER";

    /// <summary>
    /// A pre-existing, sub-keyed organisation: its <c>clerk_org_id</c> IS the user's own Clerk user
    /// id. Every organisation that predates Clerk organisations looks like this.
    /// </summary>
    private const string LegacySoloSub = "user_TEST_RBAC_LEGACY";

    private readonly RbacTestFactory _factory;

    public OrgAdminGateRefusesNonAdminsTests(RbacTestFactory factory)
    {
        _factory = factory;
        Seed();
    }

    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

        foreach (var key in new[] { ClerkOrg, LegacySoloSub })
        {
            if (db.Organisations.Any(o => o.ClerkOrgId == key)) continue;
            db.Organisations.Add(new Organisation
            {
                Id = Guid.NewGuid(),
                ClerkOrgId = key,
                Name = key,
                Slug = key.ToLowerInvariant(),
                Plan = "growth",
                AccountStatus = "active",
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.SaveChanges();
    }

    // ── The derived endpoint set ──────────────────────────────────────────────

    private sealed record GatedEndpoint(string Method, string Template, string Url);

    /// <summary>
    /// Every routed endpoint whose metadata carries the gate, with its route parameters filled in so
    /// it can actually be called. The values are deliberately arbitrary: this test only ever
    /// distinguishes "refused by the gate" from "reached the action", so a 404 for a nonexistent id
    /// is a PASS for the admin case — it proves admission.
    /// </summary>
    private IReadOnlyList<GatedEndpoint> GatedEndpoints()
    {
        var sources = _factory.Services.GetServices<EndpointDataSource>();

        return sources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<RequireOrgAdminAttribute>() is not null)
            .Select(e => new GatedEndpoint(
                e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.First() ?? "GET",
                e.RoutePattern.RawText ?? string.Empty,
                FillRouteParameters(e.RoutePattern)))
            .OrderBy(e => e.Template, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Substitutes a plausible value for every <c>{parameter}</c> in a route template — a Guid where
    /// the route constrains one, otherwise <c>1</c>.
    /// </summary>
    private static string FillRouteParameters(RoutePattern pattern) =>
        Regex.Replace(pattern.RawText ?? string.Empty, @"\{[^}]+\}", match =>
            match.Value.Contains(":guid", StringComparison.OrdinalIgnoreCase)
                ? Guid.NewGuid().ToString()
                : "1");

    [Fact]
    public void TheDiscoveredGatedSet_IsNonEmpty_AndTheSizeWeExpect()
    {
        var endpoints = GatedEndpoints();

        Assert.True(endpoints.Count > 0,
            "no endpoint in the running application carries the org-admin gate. Every other test in "
          + "this file loops over this set, so an empty one would make them all pass while enforcing "
          + "nothing.");

        Assert.True(endpoints.Count == ExpectedGatedEndpointCount,
            $"expected {ExpectedGatedEndpointCount} gated endpoints, found {endpoints.Count}:\n"
          + string.Join("\n", endpoints.Select(e => $"  • {e.Method} {e.Template}")));
    }

    /// <summary>
    /// THE DEFECT, VERBATIM: an ordinary member calling each gated endpoint. Before this packet every
    /// one of these succeeded.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryMember_IsRefusedOnEveryGatedEndpoint()
    {
        var failures = new List<string>();

        foreach (var endpoint in GatedEndpoints())
        {
            var (status, body) = await CallAsync(endpoint, role: "org:member", sub: MemberSub, org: ClerkOrg);

            if (status != (int)HttpStatusCode.Forbidden)
            {
                failures.Add($"  • {endpoint.Method} {endpoint.Template} → {status?.ToString() ?? "threw"} (expected 403)");
                continue;
            }

            if (body is null || !body.Contains(RequireOrgAdminAttribute.ErrorCode, StringComparison.Ordinal))
                failures.Add($"  • {endpoint.Method} {endpoint.Template} → 403 but without the "
                           + $"'{RequireOrgAdminAttribute.ErrorCode}' code the frontend branches on. Body: {body}");
        }

        Assert.True(failures.Count == 0,
            "a member of the organisation — not an administrator — was admitted to an action reserved "
          + "for administrators, or was refused without the machine-readable code:\n"
          + string.Join("\n", failures));
    }

    /// <summary>
    /// The other half, without which the test above would pass just as well if the gate refused
    /// EVERYONE. Any status other than 403 proves admission: the request reached the action, which is
    /// then free to answer 404/400/409 for the throwaway ids this test sends.
    /// </summary>
    [Fact]
    public async Task AnAdministrator_IsAdmittedToEveryGatedEndpoint()
    {
        var failures = new List<string>();

        foreach (var endpoint in GatedEndpoints())
        {
            var (status, body) = await CallAsync(endpoint, role: "org:admin", sub: AdminSub, org: ClerkOrg);

            if (status == (int)HttpStatusCode.Forbidden
                && body?.Contains(RequireOrgAdminAttribute.ErrorCode, StringComparison.Ordinal) == true)
            {
                failures.Add($"  • {endpoint.Method} {endpoint.Template} → refused an administrator");
            }
        }

        Assert.True(failures.Count == 0,
            "an organisation administrator was refused by the org-admin gate. A gate that refuses "
          + "everyone is not a gate, and it would make the member test above pass for the wrong "
          + "reason:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// FAIL CLOSED. A token that resolves a real Clerk organisation but carries no role claim cannot
    /// be judged, and an unjudgeable request is refused rather than admitted.
    ///
    /// <para>This repo has six recorded instances of an unrecognised or absent value falling through
    /// to success. This is the assertion that stops the seventh: it is the exact shape a
    /// misconfigured Clerk JWT template produces, and the tempting "no role means we cannot tell, so
    /// let them through" would reopen the whole finding.</para>
    /// </summary>
    [Fact]
    public async Task ATokenWithNoRoleClaim_IsRefused()
    {
        var failures = new List<string>();

        foreach (var endpoint in GatedEndpoints())
        {
            var (status, _) = await CallAsync(endpoint, role: null, sub: MemberSub, org: ClerkOrg);
            if (status != (int)HttpStatusCode.Forbidden)
                failures.Add($"  • {endpoint.Method} {endpoint.Template} → {status?.ToString() ?? "threw"} (expected 403)");
        }

        Assert.True(failures.Count == 0,
            "a request whose role could not be determined was ADMITTED. Admission must require "
          + "OrgRole.Admin specifically, never the absence of evidence that the caller is a member:\n"
          + string.Join("\n", failures));
    }

    /// <summary>
    /// BOOTSTRAP — the assertion that this change does not lock every existing customer out of their
    /// own account on the day it ships.
    ///
    /// <para>Organisations that predate Clerk organisations are keyed to a single user's own Clerk
    /// user id (<c>clerk_org_id == sub</c>), and their tokens carry no organisation and therefore no
    /// role. The middleware admits that user as the administrator of that organisation because the
    /// row it resolved is, by construction, their personal tenant and they are its only member. Note
    /// what this is NOT: it is not "no role claim → admit" — the test above proves that same
    /// role-less token is refused the moment it names a real Clerk organisation.</para>
    /// </summary>
    [Fact]
    public async Task ALegacySubKeyedOrganisation_StillAdmitsItsOwner()
    {
        var failures = new List<string>();

        foreach (var endpoint in GatedEndpoints())
        {
            // No org claim and no role claim — exactly what a pre-Clerk-organisations token carries.
            var (status, body) = await CallAsync(endpoint, role: null, sub: LegacySoloSub, org: null);

            if (status == (int)HttpStatusCode.Forbidden
                && body?.Contains(RequireOrgAdminAttribute.ErrorCode, StringComparison.Ordinal) == true)
            {
                failures.Add($"  • {endpoint.Method} {endpoint.Template} → refused");
            }
        }

        Assert.True(failures.Count == 0,
            "the sole owner of a pre-existing sub-keyed organisation was locked out of their own "
          + "billing, delivery configuration and API keys. The entire production base has this shape, "
          + "so this is the difference between shipping RBAC and taking the product away from every "
          + "current customer:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// An API key authenticates as the organisation but carries no role, so it must not be able to
    /// mint another key, cancel the subscription, or repoint deliveries. This pins the claim the
    /// handler's comment makes.
    /// </summary>
    [Fact]
    public void AnApiKeyPrincipal_CarriesNoRole_AndSoResolvesToUnknown()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("org_id", Guid.NewGuid().ToString()),
                new Claim("org_slug", "acme"),
                new Claim("auth_method", "api_key"),
                new Claim("sub", $"apikey:{Guid.NewGuid()}"),
            ],
            authenticationType: "ApiKey"));

        Assert.Equal(OrgRole.Unknown, ClerkOrgRole.FromClaims(principal));
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues one request. Returns a null status when the server threw: an exception raised INSIDE
    /// the action is still proof the gate admitted the caller, which is all the admin assertions
    /// need, and treating it as a silent pass for the member assertions would be wrong — so those
    /// compare against 403 explicitly and a null fails them.
    /// </summary>
    private async Task<(int? Status, string? Body)> CallAsync(
        GatedEndpoint endpoint, string? role, string sub, string? org)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.SubHeader, sub);
        if (role is not null) client.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, role);
        if (org is not null) client.DefaultRequestHeaders.Add(RbacTestAuthHandler.OrgHeader, org);

        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), "/" + endpoint.Url.TrimStart('/'));
        if (endpoint.Method is "POST" or "PUT" or "PATCH")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        try
        {
            using var response = await client.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception)
        {
            // The action itself threw (no Stripe customer, no supplier, …). The gate had already
            // admitted the caller by then, which is the only thing being measured here.
            return (null, null);
        }
    }
}
