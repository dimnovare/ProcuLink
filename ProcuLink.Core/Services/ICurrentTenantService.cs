namespace ProcuLink.Core.Services;

/// <summary>
/// Provides the resolved internal identifiers for the authenticated caller's tenant.
/// Populated per-request by TenantResolutionMiddleware after JWT validation.
/// </summary>
public interface ICurrentTenantService
{
    /// <summary>
    /// Internal UUID primary key of the calling organisation.
    /// Resolved from the JWT <c>org_id</c> claim via the organisations table.
    /// </summary>
    Guid OrganisationId { get; }

    /// <summary>
    /// Clerk user ID string (the JWT <c>sub</c> claim, e.g. "user_2abc…").
    /// </summary>
    string ClerkUserId { get; }
}
