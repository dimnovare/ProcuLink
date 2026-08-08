using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ProcuLink.Infrastructure.Tenancy;

/// <summary>
/// Splits the EF model cache into two models: one WITH the organisation query filters (used by a
/// context that has been scoped to an organisation) and one WITHOUT them (used by an unscoped,
/// cross-organisation context).
///
/// <para><b>Why not one model with an inert filter.</b> The obvious single-model form is
/// <c>e =&gt; scope == null || e.OrgId == scope</c>, which is inert when the scope is null. It was
/// measured and rejected: it emits
/// <c>WHERE @__ef_filter__p_0 IS NULL OR o.org_id = @__ef_filter__p_0</c> on EVERY query against
/// all 47 organisation-scoped tables, including the Worker's cross-organisation sweeps over the
/// largest ones. That predicate is not sargable under a generic plan, and the filters are not yet
/// armed anywhere in production, so the single-model form would have shipped a query-shape change
/// to 100% of production reads in exchange for zero protection.</para>
///
/// <para>With the cache split, an unscoped context produces byte-identical SQL to what it produced
/// before the filters existed, and a scoped context produces a clean sargable
/// <c>WHERE o.org_id = @p</c> that the existing <c>(org_id, …)</c> indexes serve.</para>
///
/// <para>The cost of the split is an ordering constraint — a context must be scoped BEFORE its
/// first query, because the model is resolved once per instance. That constraint is not left to
/// documentation: <see cref="ProcuLinkDbContext.ScopeToOrganisation"/> resolves the model
/// immediately and throws if the filters are absent, so arming too late fails loudly instead of
/// silently leaving the context unfiltered.</para>
/// </summary>
public sealed class OrgScopeModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(),
         (context as ProcuLinkDbContext)?.ScopedOrganisationId is not null,
         designTime);
}
