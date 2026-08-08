using System.Linq.Expressions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure.Tenancy;

/// <summary>
/// Registers EF Core global query filters so organisation scoping is the model's default
/// rather than a convention re-typed at every call site.
///
/// <para><b>Why this exists.</b> Tenancy rested entirely on hand-written
/// <c>.Where(x =&gt; x.OrgId == orgId)</c> clauses — roughly 320 of them. Every one is a place a
/// future edit can silently omit the scope, and the result is a cross-tenant read that no test
/// notices unless it happens to seed a second organisation. CLAUDE.md §11 lists "EF queries
/// without org_id scope" as a hard prohibition; before this file, nothing enforced it
/// structurally.</para>
///
/// <para><b>What is deliberately NOT changed.</b> The existing <c>.Where</c> clauses stay. Belt
/// and braces is correct while the filters are new, and the filter is inert until a caller arms
/// it (see <see cref="ProcuLinkDbContext.ScopeToOrganisation"/>), so the <c>.Where</c> clauses
/// remain the only active control on every path that has not been armed.</para>
///
/// <para><b>Discovery is by shape, not by list.</b> Filters are applied to every mapped entity
/// type that carries an organisation column, found by property name. A new entity added with an
/// <c>OrgId</c> is therefore filtered the moment it is mapped — nobody has to remember to
/// register it. An entity added WITHOUT an organisation column must be declared in
/// <see cref="DeclaredUnscoped"/> with a stated reason, and the architecture guard fails the
/// build until it is.</para>
/// </summary>
public static class OrgQueryFilters
{
    /// <summary>
    /// The two organisation-column property names in this model. Both spellings are real and
    /// load-bearing: <c>OrgId</c> is the majority convention (column <c>org_id</c>), and
    /// <c>OrganisationId</c> is used by the Wave-2 document entities, tenant API keys, schema
    /// fingerprints and integration subscriptions (column <c>organisation_id</c>). There is no
    /// z-spelling and no <c>TenantId</c> anywhere in the model; if one appears, it belongs here.
    /// </summary>
    public static readonly IReadOnlyList<string> OrgPropertyNames = ["OrgId", "OrganisationId"];

    /// <summary>
    /// Entity types that are mapped but carry no organisation column, each with the reason it is
    /// not filtered. This is a declaration, not a suppression list: the architecture guard
    /// asserts that every mapped entity type is either filtered or named here, so adding an
    /// entity forces an explicit decision instead of a silent gap.
    ///
    /// <para><b>Children scoped through a parent</b> are the largest group. They have no org
    /// column of their own and reach one only through a required FK. A navigation-based filter
    /// (<c>l =&gt; l.Order.OrgId == …</c>) is possible and is noted as a follow-up in the PR, but
    /// it adds a JOIN to every query against these types and interacts badly with the
    /// required-navigation warning, so it is deliberately out of this packet. Direct queries
    /// against these DbSets are still scoped only by their explicit <c>.Where</c> clauses.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> DeclaredUnscoped =
        new Dictionary<Type, string>
        {
            [typeof(Organisation)] =
                "The tenant root itself. Its primary key IS the organisation id that every " +
                "OrgId/OrganisationId column points at, so there is nothing to scope it against. " +
                "Filtering it would also break tenant resolution, which reads this table to " +
                "discover which organisation a request belongs to before any tenant is known.",

            [typeof(AppUser)] =
                "Cross-organisation identity. One human (one Clerk user) legitimately belongs to " +
                "many organisations; the org relationship lives on Membership, which IS filtered. " +
                "An org column here would be wrong, not merely absent.",

            [typeof(DataProtectionKey)] =
                "ASP.NET Core DataProtection key ring — framework infrastructure shared by every " +
                "API instance, not tenant data. The key ring is also loaded on a DI scope with no " +
                "HTTP request, so it must never depend on a resolved tenant.",

            [typeof(PurchaseOrderLineEntity)] =
                "Child of PurchaseOrderEntity, scoped through the required FK OrderId. The parent " +
                "carries OrgId and is filtered.",

            [typeof(SupplierAcceptanceRule)] =
                "Child of SupplierAcceptanceProfile, scoped through the required FK ProfileId. " +
                "The parent carries OrgId and is filtered.",

            [typeof(ConnectionRevisionItemMapping)] =
                "Immutable snapshot child of SupplierConnectionRevision, scoped through the " +
                "required FK RevisionId. The parent carries OrgId and is filtered.",

            [typeof(ConnectionRevisionTestCase)] =
                "Test-pack child of SupplierConnectionRevision, scoped through the required FK " +
                "RevisionId. The parent carries OrgId and is filtered.",
        };

    /// <summary>
    /// Returns the name of the organisation-scoping property on <paramref name="entityType"/>, or
    /// null when the type carries no organisation column.
    ///
    /// <para>Takes the read-only metadata interface so the same detection runs against a built
    /// model. The architecture guard must ask this question of the FINISHED model, not of the
    /// builder — otherwise the guard and the registration could disagree about what counts as
    /// org-scoped, and a guard that computes "scoped" differently from the code it checks is not
    /// checking it.</para>
    /// </summary>
    public static string? FindOrgPropertyName(IReadOnlyEntityType entityType)
    {
        foreach (var name in OrgPropertyNames)
        {
            var property = entityType.FindProperty(name);
            if (property is not null && property.ClrType == typeof(Guid))
                return property.Name;
        }

        return null;
    }

    /// <summary>
    /// Applies the organisation filter to every mapped entity type that has an organisation
    /// column. Called once from <see cref="ProcuLinkDbContext.OnModelCreating"/>.
    ///
    /// <para>The predicate is simply
    /// <c>e =&gt; e.&lt;OrgProperty&gt; == context.ScopedOrganisationId.Value</c> — no null branch. This
    /// method is only ever called for the SCOPED model; an unscoped context resolves a different,
    /// filter-free model via <see cref="OrgScopeModelCacheKeyFactory"/>, so
    /// <c>.Value</c> is always populated here. Keeping the null test out of the predicate is what
    /// makes the emitted SQL a sargable <c>WHERE org_id = @p</c> that the existing
    /// <c>(org_id, …)</c> indexes serve, instead of an unsargable
    /// <c>@p IS NULL OR org_id = @p</c>.</para>
    ///
    /// <para><b>The context reference is not baked in.</b> <see cref="Expression.Constant(object)"/>
    /// over <paramref name="context"/> would normally pin the one instance whose OnModelCreating
    /// ran, and the model is cached and reused by every later instance. EF Core rewrites DbContext
    /// references inside query filters to the *executing* context before compiling the query, so
    /// each instance reads its own scope. That rewrite is the single load-bearing assumption in
    /// this file, so it is proved directly by a test that arms two contexts differently against
    /// one cached model rather than being taken on trust.</para>
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder, ProcuLinkDbContext context)
    {
        // context.ScopedOrganisationId.Value — a Guid, re-read per query execution.
        var scopeValue = Expression.Property(
            Expression.Property(
                Expression.Constant(context),
                nameof(ProcuLinkDbContext.ScopedOrganisationId)),
            nameof(Nullable<Guid>.Value));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Owned types and inheritance children cannot carry their own filter; EF requires the
            // filter on the root. The model has neither today, but the guard is cheap and keeps
            // this loop correct if one is introduced.
            if (entityType.IsOwned() || entityType.BaseType is not null)
                continue;

            var orgPropertyName = FindOrgPropertyName(entityType);
            if (orgPropertyName is null)
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");

            // EF.Property<Guid>(e, "OrgId") rather than a CLR member access, so shadow properties
            // and differing declaring types both work.
            var orgAccess = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                [typeof(Guid)],
                parameter,
                Expression.Constant(orgPropertyName));

            var body = Expression.Equal(orgAccess, scopeValue);

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }
}
