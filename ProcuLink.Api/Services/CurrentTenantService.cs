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

    /// <summary>
    /// The two throws below look alike and are deliberately DIFFERENT types.
    ///
    /// <para>No resolved organisation is not a server fault and not a refusal — it is "not yet",
    /// and it is answered 503 + Retry-After by
    /// <see cref="ProcuLink.Api.Middleware.TenantNotResolvedExceptionHandler"/>. No sub claim means
    /// there is no authenticated user at all, which is a different condition and keeps the type it
    /// has always had. Sharing <see cref="UnauthorizedAccessException"/> between them is what made
    /// the first one unmappable.</para>
    /// </summary>
    public Guid OrganisationId =>
        _ctx.HttpContext?.Items[Items.OrganisationId] is Guid id
            ? id
            : throw new TenantNotResolvedException();

    public string ClerkUserId =>
        _ctx.HttpContext?.User.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("No sub claim found — user not authenticated.");
}
