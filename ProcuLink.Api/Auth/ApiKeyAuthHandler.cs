using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Security;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Auth;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authenticates machine-to-machine requests carrying X-ProcuLink-Key header.
/// On success: sets org_id, org_slug, auth_method=api_key, key_id claims.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
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

        // Fire-and-forget LastUsedAt update (non-critical)
        apiKey.LastUsedAt = DateTime.UtcNow;
        _ = _db.SaveChangesAsync(CancellationToken.None);

        var claims = new[]
        {
            new Claim("org_id",      apiKey.OrganisationId.ToString()),
            new Claim("org_slug",    apiKey.Organisation.Slug),
            new Claim("auth_method", "api_key"),
            new Claim("key_id",      apiKey.Id.ToString()),
        };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
