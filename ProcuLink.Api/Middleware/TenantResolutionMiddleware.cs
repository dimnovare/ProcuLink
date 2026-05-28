using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Middleware;

/// <summary>
/// Runs after authentication. For authenticated requests that carry an org_id claim,
/// looks up the internal Organisation UUID and stores it in HttpContext.Items so that
/// CurrentTenantService can serve it synchronously throughout the rest of the pipeline.
///
/// If the org_id is present but no matching DB record exists, the organisation is
/// auto-provisioned on the spot (pilot trial, 14-day window). This covers the first
/// login flow where Clerk creates an org before the back-end has ever seen it.
///
/// Unauthenticated requests (e.g. /health) pass through untouched.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ProcuLinkDbContext db, IAnalyticsService analytics)
    {
        var sub = context.User.FindFirst("sub")?.Value;

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Prefer the Clerk org_id claim. When no Clerk organisation is active in
            // the session (e.g. personal account, or the session hasn't activated the
            // org yet) fall back to the user's sub claim so each user still maps to a
            // tenant. Clerk user IDs start with "user_" and org IDs with "org_", so
            // they share no namespace and won't collide.
            var clerkOrgId = context.User.FindFirst("org_id")?.Value;
            var orgSlug    = context.User.FindFirst("org_slug")?.Value;
            var fellBackToUser = false;

            if (string.IsNullOrEmpty(clerkOrgId) && !string.IsNullOrEmpty(sub))
            {
                clerkOrgId     = sub;
                orgSlug        = "Personal workspace";
                fellBackToUser = true;
            }

            if (!string.IsNullOrEmpty(clerkOrgId))
            {
                var org = await db.Organisations
                    .AsNoTracking()
                    .Where(o => o.ClerkOrgId == clerkOrgId)
                    .Select(o => new { o.Id })
                    .FirstOrDefaultAsync(context.RequestAborted);

                if (org is null)
                {
                    // Auto-provision: first time this tenant key contacts the API.
                    var now = DateTime.UtcNow;
                    var orgName = orgSlug ?? clerkOrgId;
                    var newOrg = new Organisation
                    {
                        Id             = Guid.NewGuid(),
                        ClerkOrgId     = clerkOrgId,
                        Name           = orgName,
                        Slug           = GenerateSlug(orgName),
                        Plan           = "pilot",
                        AccountStatus  = "trialing",
                        CreatedAt      = now,
                        TrialStartedAt = now,
                        TrialEndsAt    = now.AddDays(14),
                    };

                    db.Organisations.Add(newOrg);
                    await db.SaveChangesAsync(context.RequestAborted);

                    _logger.LogInformation(
                        "Auto-provisioned organisation '{Name}' (TenantKey={ClerkOrgId}, FellBackToUser={Fallback}).",
                        newOrg.Name, clerkOrgId, fellBackToUser);

                    await analytics.CaptureAsync(
                        organisationId: newOrg.Id,
                        userId: sub,
                        eventName: "org_created",
                        properties: new Dictionary<string, object?>
                        {
                            ["plan"] = "pilot",
                            ["created_via"] = "signup_flow",
                        },
                        ct: context.RequestAborted);

                    context.Items[CurrentTenantService.Items.OrganisationId] = newOrg.Id;
                }
                else
                {
                    context.Items[CurrentTenantService.Items.OrganisationId] = org.Id;
                }
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Generates a unique kebab-case slug from the org name.
    /// Appends a 4-char random suffix to ensure uniqueness without a DB round-trip.
    /// </summary>
    private static string GenerateSlug(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        // Collapse consecutive dashes
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "org";
        // 4-char random suffix for uniqueness
        slug += "-" + Guid.NewGuid().ToString("N")[..4];
        return slug;
    }
}
