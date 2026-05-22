using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Middleware;

/// <summary>
/// Runs after authentication. For authenticated requests that carry an org_id claim,
/// looks up the internal Organisation UUID and stores it in HttpContext.Items so that
/// CurrentTenantService can serve it synchronously throughout the rest of the pipeline.
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
                    _logger.LogWarning(
                        "Authenticated request with unknown org_id '{ClerkOrgId}' — organisation not provisioned.",
                        clerkOrgId);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Organisation not provisioned. Contact support."
                    });
                    return;
                }

                context.Items[CurrentTenantService.Items.OrganisationId] = org.Id;
            }
        }

        await _next(context);
    }
}
