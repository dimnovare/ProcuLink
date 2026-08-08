using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services;
using ProcuLink.Core.Security;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Auth;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authenticates machine-to-machine requests carrying X-ProcuLink-Key header.
/// On success: sets org_id, org_slug, auth_method=api_key, key_id claims AND
/// stores the resolved internal organisation UUID in HttpContext.Items under the
/// SAME key TenantResolutionMiddleware uses for the JWT path. This unifies tenant
/// resolution across both auth schemes onto one value-space (internal Guid in
/// Items), so API-key controllers resolve the org via ICurrentTenantService just
/// like JWT controllers, instead of parsing the org_id claim themselves.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    /// <summary>
    /// Minimum interval between LastUsedAt writes for a given key. A high-volume
    /// integration POSTing every few seconds would otherwise issue one UPDATE per
    /// request on the hot auth path; this throttles those to at most one write per
    /// window while keeping LastUsedAt fresh enough for "last seen" displays.
    /// </summary>
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    private readonly ProcuLinkDbContext _db;
    private readonly string _hashSecret;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ProcuLinkDbContext db,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _db = db;
        _hashSecret = configuration["Security:ApiKeyHashSecret"]
                      ?? throw new InvalidOperationException(
                          "Security:ApiKeyHashSecret is not configured. " +
                          "Set this via environment variable SECURITY__APIKEYHASHSECRET or appsettings.");
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-ProcuLink-Key", out var keyValues))
            return AuthenticateResult.NoResult();

        var rawKey = keyValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length < 16)
            return AuthenticateResult.Fail("Invalid API key format.");

        var hash = ApiKeyHasher.ComputeHash(rawKey, _hashSecret);

        var apiKey = await _db.TenantApiKeys
            .Include(k => k.Organisation)
            .Where(k => k.KeyHash == hash && k.IsActive)
            .FirstOrDefaultAsync();

        if (apiKey is null)
            return AuthenticateResult.Fail("API key not found or inactive.");

        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
            return AuthenticateResult.Fail("API key expired.");

        var now = DateTime.UtcNow;

        // Unify tenant resolution across auth schemes: publish the resolved internal
        // organisation UUID under the SAME HttpContext.Items key that
        // TenantResolutionMiddleware uses for the JWT path. API-key controllers then
        // resolve the org via ICurrentTenantService — one resolver, one value-space.
        Context.Items[CurrentTenantService.Items.OrganisationId] = apiKey.OrganisationId;

        // LastUsedAt: best-effort, throttled, and run on its OWN DI scope so it never
        // races the request's scoped DbContext (the previous un-awaited
        // `_ = _db.SaveChangesAsync(...)` shared `_db` with the controller action and
        // could interleave with the next query on the same context). Throttling keeps
        // the hot auth path fast for high-volume callers.
        if (apiKey.LastUsedAt is null || now - apiKey.LastUsedAt.Value >= LastUsedThrottle)
            TouchLastUsedAsync(apiKey.Id, now);

        var claims = new[]
        {
            new Claim("org_id",      apiKey.OrganisationId.ToString()),
            new Claim("org_slug",    apiKey.Organisation.Slug),
            new Claim("auth_method", "api_key"),
            new Claim("key_id",      apiKey.Id.ToString()),
            // M3 (plan 2026-06-12): a stable per-key `sub` so consumers that partition on
            // the subject claim (e.g. the rate limiter's PartitionKey in Program.cs) can
            // limit per API key instead of per source IP. Prefixed so it can never collide
            // with a Clerk user id.
            new Claim("sub",         $"apikey:{apiKey.Id}"),
            // Deliberately NO role claim. An API key is machine access minted by an administrator
            // for one integration; it is not that administrator. Without a role it resolves to
            // OrgRole.Unknown, so RequireOrgAdminAttribute refuses it — a leaked key cannot mint
            // another key, cancel the subscription, repoint a supplier's deliveries, or override
            // the acceptance gate. The ingest surface it exists for (/api/ingress/*) is ungated.
        };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Updates LastUsedAt for the given key on a fresh, detached DI scope so it cannot
    /// race the request-scoped DbContext. Fire-and-forget: failures are logged and
    /// swallowed — a missed "last used" timestamp must never fail authentication.
    /// </summary>
    private void TouchLastUsedAsync(Guid keyId, DateTime usedAt)
    {
        var scopeFactory = Context.RequestServices.GetService<IServiceScopeFactory>();
        if (scopeFactory is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
                await db.TenantApiKeys
                    .Where(k => k.Id == keyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, usedAt));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Non-critical: failed to update LastUsedAt for API key {KeyId}.", keyId);
            }
        });
    }
}
