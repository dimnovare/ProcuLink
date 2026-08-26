namespace ProcuLink.Core.Services;

/// <summary>
/// Thrown by <see cref="ICurrentTenantService.OrganisationId"/> when the request carries no
/// resolved organisation.
///
/// <para><b>Why this is its own type.</b> It used to be an
/// <see cref="UnauthorizedAccessException"/> — the same type
/// <see cref="ICurrentTenantService.ClerkUserId"/> still throws for "no sub claim, there is no
/// authenticated user at all". Those are different conditions with different honest answers, and
/// one shared type meant the edge could not tell them apart. So neither was mapped, and an
/// unresolved tenant reached the client as a 500: a server fault, for a request that was simply
/// early.
///
/// <para>A dedicated type is what lets
/// <c>ProcuLink.Api.Middleware.TenantNotResolvedExceptionHandler</c> answer this one case
/// precisely, without the mapping ever drifting onto its sibling.</para></para>
///
/// <para><b>When it happens in production.</b> A brand-new organisation's first page load: Clerk
/// mints the session token before the organisation claim is attached to it, so the token
/// authenticates a real user who — for a moment — belongs to no organisation this request can see.
/// The condition clears on its own within seconds. See
/// <c>ProcuLink.Api.Middleware.TenantResolutionMiddleware</c> for the sibling condition (tenant
/// resolution could not reach the database), which is answered the same way for the same
/// reason.</para>
/// </summary>
public sealed class TenantNotResolvedException : Exception
{
    /// <summary>The message this condition has always carried; kept verbatim so log searches for
    /// it still find it after the type change.</summary>
    public const string DefaultMessage =
        "Organisation not resolved. Ensure the request is authenticated and the org_id claim is present.";

    public TenantNotResolvedException() : base(DefaultMessage) { }

    public TenantNotResolvedException(string message) : base(message) { }

    public TenantNotResolvedException(string message, Exception innerException)
        : base(message, innerException) { }
}
