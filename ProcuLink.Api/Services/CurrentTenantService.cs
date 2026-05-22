using ProcuLink.Core.Services;

namespace ProcuLink.Api.Services;

/// <summary>
/// Reads the tenant context that TenantResolutionMiddleware stored in HttpContext.Items
/// after resolving the Clerk org_id claim to our internal UUID.
/// </summary>
public sealed class CurrentTenantService : ICurrentTenantService
{
    // Shared keys used between middleware and this service.
    internal static class Items
    {
        public const string OrganisationId = "ProcuLink.OrganisationId";
    }

    private readonly IHttpContextAccessor _ctx;

    public CurrentTenantService(IHttpContextAccessor ctx) => _ctx = ctx;

    public Guid OrganisationId =>
        _ctx.HttpContext?.Items[Items.OrganisationId] is Guid id
            ? id
            : throw new UnauthorizedAccessException(
                "Organisation not resolved. Ensure the request is authenticated and the org_id claim is present.");

    public string ClerkUserId =>
        _ctx.HttpContext?.User.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("No sub claim found — user not authenticated.");
}
