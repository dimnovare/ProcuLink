namespace ProcuLink.Infrastructure.Tenancy;

/// <summary>
/// Declares that an endpoint deliberately reads across organisations, so the request pipeline must
/// NOT arm the organisation query filters for it.
///
/// <para><b>Why this exists.</b> Once <c>TenantResolutionMiddleware</c> arms the request-scoped
/// <see cref="ProcuLinkDbContext"/>, every org-scoped read on that context is silently restricted to
/// the caller's own organisation. For an endpoint that is supposed to see the whole system — the
/// platform-owner admin surface, an ops health count — that restriction does not produce an error.
/// It produces a smaller number that looks exactly like a correct one: an admin page reporting one
/// tenant's orders as the platform total. That is this codebase's most-repeated defect shape, so the
/// opt-out is a declaration the pipeline reads, not a convention a reviewer has to notice.</para>
///
/// <para><b>Explicit and greppable, never ambient.</b> Every cross-organisation read on an HTTP path
/// is listed by one search:
/// <code>git grep -n "CrossOrganisationRead" -- ProcuLink.Api</code>
/// There is no configuration file, no route-prefix convention and no allowlist elsewhere that can
/// also suppress arming — the attribute is the only way, so the search is complete by construction.
/// <c>OrgQueryFilterArmingTests</c> pins the resulting inventory, so adding or removing one is a
/// deliberate, reviewed change rather than a diff nobody looked at.</para>
///
/// <para><b>It opts out the whole request, not one query.</b> Scoping is per-DbContext and the
/// request shares one instance across the controller and every service it calls, so an endpoint
/// carrying this attribute leaves the entire request unscoped. That is deliberate: the alternative —
/// arming the request and having the cross-tenant reader resolve a second context from a child DI
/// scope — puts the opt-out at a call site the endpoint's author never sees. Unscoped is also
/// exactly the behaviour every one of these endpoints had before arming, and the hand-written
/// <c>.Where(x =&gt; x.OrgId == orgId)</c> clauses remain in place, so an opted-out endpoint is no
/// weaker than it was. Admission control is a separate concern and stays where it was: the admin
/// surface is gated by <c>[AdminOnly]</c>.</para>
/// </summary>
/// <remarks>
/// Applied to a controller class, it covers every action on it — which is what the platform-owner
/// surface wants, since a new cross-tenant action added there inherits the opt-out instead of
/// silently truncating. Applied to a single action, it covers only that action.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class CrossOrganisationReadAttribute : Attribute
{
    /// <param name="reason">
    /// Why this endpoint must see other organisations' rows. Recorded on the context as
    /// <see cref="ProcuLinkDbContext.CrossOrganisationReason"/> so an unscoped context can say why it
    /// is unscoped. Required and non-blank: an opt-out nobody had to justify is the one that gets
    /// copied onto an endpoint that did not need it.
    /// </param>
    public CrossOrganisationReadAttribute(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A cross-organisation endpoint must state why it reads across tenants.",
                nameof(reason));

        Reason = reason;
    }

    /// <summary>Why this endpoint reads across organisations.</summary>
    public string Reason { get; }
}
