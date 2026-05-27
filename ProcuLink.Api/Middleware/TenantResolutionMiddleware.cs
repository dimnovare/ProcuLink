using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
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

    public async Task InvokeAsync(HttpContext context, ProcuLinkDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Prefer the Clerk org_id claim. When no Clerk organisation is active in
            // the session (e.g. personal account, or the session hasn't activated the
            // org yet) fall back to the user's sub claim so each user still maps to a
            // tenant. Clerk user IDs start with "user_" and org IDs with "org_", so
            // they share no namespace and won't collide.
            var clerkOrgId = context.User.FindFirst("org_id")?.Value;
            var orgSlug    = context.User.FindFirst("org_slug")?.Value;
            var sub        = context.User.FindFirst("sub")?.Value;
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
                    var newOrg = new Organisation
                    {
                        Id             = Guid.NewGuid(),
                        ClerkOrgId     = clerkOrgId,
                        Name           = orgSlug ?? clerkOrgId,
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
}
