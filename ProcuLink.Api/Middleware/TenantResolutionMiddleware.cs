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
            var clerkOrgId = context.User.FindFirst("org_id")?.Value;

            if (!string.IsNullOrEmpty(clerkOrgId))
            {
                var org = await db.Organisations
                    .AsNoTracking()
                    .Where(o => o.ClerkOrgId == clerkOrgId)
                    .Select(o => new { o.Id })
                    .FirstOrDefaultAsync(context.RequestAborted);

                if (org is null)
                {
                    // Auto-provision: first time this Clerk org contacts the API.
                    // Use org_slug as the display name; fall back to the raw org_id.
                    var orgSlug = context.User.FindFirst("org_slug")?.Value;
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
                        "Auto-provisioned organisation '{Name}' (ClerkOrgId={ClerkOrgId}).",
                        newOrg.Name, clerkOrgId);

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
