using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// An organisation column has to be backed by a foreign key, or it is only a number that resembles
/// a tenant.
///
/// <para><b>The defect this exists to catch.</b> A 2026-08-25 model audit asked every mapped entity
/// "do you carry an organisation column, and is there a constraint behind it?" and fifteen answered
/// yes then no. Nine of them lead an index with that column, so every query against them reads as
/// though the tenancy were enforced — <c>idempotency_keys</c>, <c>ai_usage_monthly</c>,
/// <c>overage_billing_records</c>, <c>org_plan_history</c>, <c>imported_sftp_files</c>,
/// <c>imported_s3_objects</c>, <c>email_import_records</c>, <c>canonical_field_defs</c>,
/// <c>order_parties</c>, and <c>SchemaFingerprints</c>, which the audit's own count missed because
/// it is mapped by convention rather than by an explicit <c>HasColumnName("org_id")</c> and so is
/// invisible to a source grep. Five more carried the column without an index on it.</para>
///
/// <para><b>Why the sibling guard could not see it.</b>
/// <see cref="OrgQueryFilterCoverageTests"/> walks the same corpus and asks a different question —
/// is this entity FILTERED? Every one of the fifteen was. A query filter keeps one tenant from
/// reading another's rows; it says nothing about whether the id in the column names a tenant that
/// exists, which is what an org delete, a raw-SQL delete, or a mis-computed write turns into
/// orphaned rows. The two guards are deliberately separate questions about the same column.</para>
///
/// <para>The corpus is derived from the model, using the same
/// <see cref="OrgQueryFilters.FindOrgPropertyName"/> the registration itself uses, so a guard that
/// disagreed with the code about what counts as org-scoped is not possible, and a table added
/// tomorrow is covered without anyone editing this file.</para>
/// </summary>
public sealed class OrgForeignKeyCoverageTests
{
    /// <summary>
    /// Anti-vacuity floor. Every assertion here is of the form "the offending set is empty", which
    /// a sweep that enumerates nothing satisfies perfectly. 47 entity types carry an organisation
    /// column today (the same measurement <see cref="OrgQueryFilterCoverageTests"/> is built on).
    /// The floor sits under that so ordinary churn does not trip it, but a broken model build or a
    /// renamed org property fails loudly instead of reporting a clean sweep over nothing. Raise it
    /// as the schema grows; never lower it to make a red build green.
    /// </summary>
    private const int MinimumOrgScopedEntities = 40;

    /// <summary>
    /// The tables whose organisation foreign key must REFUSE an organisation delete rather than
    /// follow it, because the row is evidence of money. Hand-written on purpose: this is the one
    /// judgement in the file, and it should have to be edited in the open to change.
    ///
    /// <para><b><c>OrgPlanHistory</c> is deliberately NOT here</b>, and the reason is the trap this
    /// map exists to keep visible. It carries the same argument — the as-of metering reader resolves
    /// the plan and order-limit override for each billed window out of it, so it is the working
    /// behind every overage invoice — but <c>ProcuLinkDbContext.AppendOrgPlanHistoryAsync</c> writes
    /// a baseline row for EVERY organisation at creation, including a free Pilot that is never
    /// charged. Restricting on it would not mean "billing evidence blocks this delete"; it would
    /// mean no organisation can ever be deleted. A constraint that fires for every row is not a
    /// decision about any of them. Pinned behaviourally by
    /// <c>OrgForeignKeyIntegrityPostgresTests.EveryNewOrganisationAlreadyHasPlanHistory_SoItCannotBeWhatBlocksADelete</c>.</para>
    /// </summary>
    private static readonly Dictionary<Type, string> RestrictedBillingEvidence = new()
    {
        [typeof(OverageBillingRecord)] =
            "The record of money actually charged through Stripe. A row exists only when money " +
            "moved, so the block is a decision about that organisation rather than a blanket ban. " +
            "Cascading it away would destroy the only proof of what was billed and to whom.",
    };

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"org-fk-coverage-{Guid.NewGuid()}")
            .Options);

    private static IModel BuildModel() => NewDb().Model;

    /// <summary>
    /// The design-time model, which is the only one that keeps index sort directions: the
    /// read-optimized runtime model drops <c>IsDescending</c> and throws when it is read.
    /// </summary>
    private static IModel BuildDesignTimeModel() =>
        NewDb().GetService<IDesignTimeModel>().Model;

    private static IReadOnlyList<IEntityType> OrgScopedEntities() =>
        BuildModel().GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null)
            .Where(e => OrgQueryFilters.FindOrgPropertyName(e) is not null)
            .OrderBy(e => e.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The organisation foreign key on this entity, or null when it has none.</summary>
    private static IForeignKey? OrgForeignKey(IEntityType entity)
    {
        var orgProperty = OrgQueryFilters.FindOrgPropertyName(entity);
        return entity.GetForeignKeys().SingleOrDefault(fk =>
            fk.PrincipalEntityType.ClrType == typeof(Organisation)
            && fk.Properties.Count == 1
            && fk.Properties[0].Name == orgProperty);
    }

    // ── direction 1: an organisation column with nothing behind it ────────────

    [Fact]
    public void EveryEntityCarryingAnOrganisationColumn_HasAForeignKeyBehindIt()
    {
        var entities = OrgScopedEntities();

        entities.Count.Should().BeGreaterThanOrEqualTo(MinimumOrgScopedEntities,
            $"the model sweep found only {entities.Count} org-scoped entity type(s), which is fewer " +
            "than this schema is known to carry — the sweep is broken, not the schema, and every " +
            "assertion below would pass over an almost-empty corpus");

        var unconstrained = entities
            .Where(e => OrgForeignKey(e) is null)
            .Select(e => $"{e.ClrType.Name} ({e.GetTableName()}.{OrgQueryFilters.FindOrgPropertyName(e)})")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unconstrained.Should().BeEmpty(
            "these entities carry an organisation column that the database never checks. An index " +
            "on it makes the lookup fast; only a foreign key makes the value mean anything. Without " +
            "one, an organisation delete, a raw-SQL delete, or a write that computed the wrong id " +
            "leaves rows referring to a tenant that does not exist — which is exactly how " +
            "order_supplier_suggestions kept a dangling order id and an operator's Clerk user id " +
            "after a GDPR erasure. Configure HasOne<Organisation>().WithMany().HasForeignKey(...) " +
            "with a deliberate OnDelete, and state the delete behaviour and its reason in the " +
            "migration");
    }

    // ── direction 2: the delete behaviour is a decision, not a default ────────

    [Fact]
    public void BillingEvidence_RefusesAnOrganisationDelete_AndEverythingElseFollowsIt()
    {
        var entities = OrgScopedEntities();

        // Unconditional floor first: the two loops below both live inside enumerations, so an
        // empty corpus or an empty judgement map would make this test pass by iterating nothing.
        RestrictedBillingEvidence.Should().NotBeEmpty(
            "the RESTRICT set is the one judgement this file hand-declares; an empty one means the " +
            "billing-evidence assertion below asserts nothing at all");
        entities.Count.Should().BeGreaterThanOrEqualTo(MinimumOrgScopedEntities);

        foreach (var (clrType, reason) in RestrictedBillingEvidence)
        {
            var entity = entities.SingleOrDefault(e => e.ClrType == clrType);
            entity.Should().NotBeNull(
                $"{clrType.Name} is declared as billing evidence but is no longer an org-scoped " +
                "mapped entity — delete the entry rather than leaving a rule standing for a table " +
                "that changed shape");

            OrgForeignKey(entity!)!.DeleteBehavior.Should().Be(DeleteBehavior.Restrict,
                $"{clrType.Name}: {reason}");
        }

        // The other arm, and it has to be populated too — if every org FK were Restrict the loop
        // above would still pass while the schema had become undeletable for the wrong reason.
        var cascading = entities
            .Where(e => !RestrictedBillingEvidence.ContainsKey(e.ClrType))
            .Where(e => OrgForeignKey(e) is not null)
            .ToList();

        cascading.Should().NotBeEmpty(
            "no org-scoped entity cascades from its organisation, which cannot be right — the FK " +
            "read is broken");

        var notCascading = cascading
            .Where(e => OrgForeignKey(e)!.DeleteBehavior != DeleteBehavior.Cascade)
            .Select(e => $"{e.ClrType.Name} ({OrgForeignKey(e)!.DeleteBehavior})")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        notCascading.Should().BeEmpty(
            "every org-scoped table that is not declared billing evidence follows its organisation " +
            "on delete. A table that should outlive the tenant belongs in RestrictedBillingEvidence " +
            "with the reason, where the decision is visible, not in a one-off OnDelete nobody sees");
    }

    // ── the audit listing's index, which is not about foreign keys at all ─────

    [Fact]
    public void TheOrgWideAuditListing_HasAnIndexThatCanServeItsSort()
    {
        var auditEvents = BuildDesignTimeModel().FindEntityType(typeof(AuditEvent));
        auditEvents.Should().NotBeNull();

        var index = auditEvents!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "IX_audit_events_org_id_created_at_desc");

        index.Should().NotBeNull(
            "AuditController's org-wide listing filters on org_id, orders by created_at descending " +
            "and pages with Skip/Take, with no entity predicate. The only other index leads " +
            "(org_id, entity_type, entity_id), so with no equality on entity_type the ordering it " +
            "provides is unusable and every listing reads the organisation's whole audit history " +
            "and sorts it");

        index!.Properties.Select(p => p.Name).Should().Equal(
            nameof(AuditEvent.OrgId), nameof(AuditEvent.CreatedAt));

        // The direction is the point, not a detail: ascending on created_at would still need a
        // backward scan, and the query's own direction is descending.
        index.IsDescending.Should().Equal(new[] { false, true },
            "org_id ascending, created_at DESCENDING — matching the listing's ORDER BY so paging " +
            "stays a forward index scan rather than a sort");
    }
}
