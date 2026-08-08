using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Tenancy used to rest entirely on roughly 320 hand-written <c>.Where(x =&gt; x.OrgId == orgId)</c>
/// clauses, with zero <c>HasQueryFilter</c> anywhere in the solution. This guard is the structural
/// half of the fix: it asserts the model itself carries an organisation filter on every entity
/// that has an organisation column.
///
/// <para>It is written to catch the NEXT instance, which arrives from two directions:</para>
/// <list type="number">
///   <item><description>A new entity is added with an <c>OrgId</c> and no filter — caught by
///   <see cref="EveryEntityWithAnOrgColumn_HasAnOrganisationQueryFilter"/>.</description></item>
///   <item><description>An <c>IgnoreQueryFilters()</c> call is added later with no stated reason —
///   caught by <see cref="EveryIgnoreQueryFiltersCall_IsDeclaredWithAReason"/>, whose detector is
///   itself pinned against a synthetic offender because the real population is currently
///   zero.</description></item>
/// </list>
///
/// <para>Behaviour — that an unpredicated query actually cannot see another organisation's rows —
/// is proved separately, against a real second organisation, by
/// <c>OrgQueryFilterIsolationTests</c> (EF InMemory) and <c>OrgQueryFilterPostgresTests</c>
/// (real Npgsql, which additionally proves the predicate translates to SQL).</para>
/// </summary>
public sealed class OrgQueryFilterCoverageTests
{
    /// <summary>
    /// Anti-vacuity floor. Every assertion below is of the form "the set of unfiltered entities is
    /// empty", which a sweep that enumerates NOTHING satisfies perfectly. Measured, not guessed:
    /// the model maps 54 entity types today. The floor sits just under that so ordinary churn does
    /// not trip it, but a broken model build or a renamed context fails loudly instead of
    /// reporting a clean sweep over an empty model. Raise it as the schema grows; never lower it
    /// to make a red build green.
    /// </summary>
    private const int MinimumMappedEntityTypes = 45;

    /// <summary>
    /// Companion floor, and the one that actually matters. A file/entity count proves the sweep
    /// found the model; only this proves the sweep found the thing it is looking for. 47 entity
    /// types carry an organisation column today. A filter registration that silently stops
    /// matching — a renamed property, a changed CLR type, an <c>Apply</c> call dropped from
    /// <c>OnModelCreating</c> — would leave the "unfiltered set is empty" assertions technically
    /// true while protecting nothing.
    /// </summary>
    private const int MinimumOrgScopedEntityTypes = 40;

    /// <summary>
    /// Entity types the sweep is known to find today. A floor, not a census: new org-scoped
    /// entities are expected and are judged by the assertions below. These specific ones
    /// disappearing means the sweep has stopped seeing the real model. PurchaseOrderEntity is the
    /// order table itself; TenantApiKey and Membership are included because they are the two most
    /// likely to be "helpfully" reclassified as infrastructure and dropped from scoping.
    /// </summary>
    private static readonly string[] KnownOrgScopedEntities =
    [
        nameof(ProcuLink.Core.Entities.PurchaseOrderEntity),
        nameof(ProcuLink.Core.Entities.Supplier),
        nameof(ProcuLink.Core.Entities.ItemMapping),
        nameof(ProcuLink.Core.Entities.OutboundArtifact),
        nameof(ProcuLink.Core.Entities.DeliveryAttempt),
        nameof(ProcuLink.Core.Entities.AuditEvent),
        nameof(ProcuLink.Core.Entities.TenantApiKey),
        nameof(ProcuLink.Core.Entities.Membership),
    ];

    /// <summary>
    /// Builds the SCOPED model — the one that carries the filters. A context is scoped before its
    /// model is resolved, exactly as production would arm it; an unscoped context deliberately
    /// resolves a separate, filter-free model (see OrgScopeModelCacheKeyFactory), and asserting
    /// against that one would be asserting against the wrong thing.
    /// </summary>
    private static IModel BuildScopedModel()
    {
        var context = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase($"model-only-{Guid.NewGuid()}")
                .Options);

        context.ScopeToOrganisation(Guid.NewGuid());
        return context.Model;
    }

    private static IReadOnlyList<IEntityType> MappedEntityTypes() =>
        BuildScopedModel().GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null)
            .OrderBy(e => e.ClrType.Name, StringComparer.Ordinal)
            .ToList();

    // ── direction 1: a new entity with an OrgId and no filter ────────────────

    [Fact]
    public void EveryEntityWithAnOrgColumn_HasAnOrganisationQueryFilter()
    {
        var entities = MappedEntityTypes();

        entities.Count.Should().BeGreaterThanOrEqualTo(MinimumMappedEntityTypes,
            $"the sweep found only {entities.Count} mapped entity type(s) on ProcuLinkDbContext, " +
            "which is fewer than this model is known to contain — the model build itself is " +
            "broken, and every assertion in this file is passing over an empty set");

        var orgScoped = entities
            .Where(e => OrgQueryFilters.OrgPropertyNames
                .Any(name => e.FindProperty(name)?.ClrType == typeof(Guid)))
            .ToList();

        orgScoped.Count.Should().BeGreaterThanOrEqualTo(MinimumOrgScopedEntityTypes,
            $"only {orgScoped.Count} entity type(s) were detected as carrying an organisation " +
            "column; this model is known to have many more, so the org-property detection is " +
            "broken and the filter is being applied to almost nothing");

        var foundNames = orgScoped.Select(e => e.ClrType.Name).ToHashSet(StringComparer.Ordinal);
        var missingKnown = KnownOrgScopedEntities.Where(k => !foundNames.Contains(k)).ToList();
        missingKnown.Should().BeEmpty(
            "these entity types are known to be organisation-scoped; not finding them means the " +
            "sweep has stopped seeing the real model rather than that they stopped being scoped");

        var unfiltered = orgScoped
            .Where(e => e.GetQueryFilter() is null)
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        unfiltered.Should().BeEmpty(
            "every entity type carrying an organisation column must have a global query filter, " +
            "so that a query written WITHOUT an explicit .Where(x => x.OrgId == orgId) still " +
            "cannot return another organisation's rows. Entities listed here are scoped only by " +
            "whatever the call site remembered to write");
    }

    [Fact]
    public void EveryEntityWithoutAnOrgColumn_IsDeclaredUnscopedWithAReason()
    {
        var entities = MappedEntityTypes();

        entities.Count.Should().BeGreaterThanOrEqualTo(MinimumMappedEntityTypes,
            $"the sweep found only {entities.Count} mapped entity type(s) — the model build is " +
            "broken, so the assertion below would pass over an empty set");

        var undeclared = entities
            .Where(e => OrgQueryFilters.FindOrgPropertyName(e) is null)
            .Where(e => !OrgQueryFilters.DeclaredUnscoped.ContainsKey(e.ClrType))
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        undeclared.Should().BeEmpty(
            "an entity with no organisation column is either genuinely global or is missing its " +
            "scoping column, and the difference cannot be guessed. Declare each one in " +
            "OrgQueryFilters.DeclaredUnscoped with the reason it is not tenant data");

        OrgQueryFilters.DeclaredUnscoped.Values.Should().OnlyContain(
            reason => reason.Length > 40,
            "a declared exclusion has to state an actual reason — a placeholder is how a missing " +
            "org column gets waved through");
    }

    /// <summary>
    /// The reverse direction. A declaration that has outlived its entity — or an entity that has
    /// since GAINED an org column while staying on the exclusion list — would leave a real table
    /// permanently unfiltered while the list above still reads as deliberate.
    /// </summary>
    [Fact]
    public void NoDeclaredExclusion_IsStale()
    {
        var entities = MappedEntityTypes();
        var mapped = entities.ToDictionary(e => e.ClrType, e => e);

        OrgQueryFilters.DeclaredUnscoped.Should().NotBeEmpty(
            "the exclusion list is the record of which tables are deliberately global; an empty " +
            "one means the declaration mechanism has been bypassed, not that everything is scoped");

        var notInModel = OrgQueryFilters.DeclaredUnscoped.Keys
            .Where(t => !mapped.ContainsKey(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        notInModel.Should().BeEmpty(
            "these types are declared unscoped but are no longer mapped by ProcuLinkDbContext — " +
            "the declaration is stale and should be deleted");

        var nowScopable = OrgQueryFilters.DeclaredUnscoped.Keys
            .Where(t => mapped.TryGetValue(t, out var e) && OrgQueryFilters.FindOrgPropertyName(e) is not null)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        nowScopable.Should().BeEmpty(
            "these types have GAINED an organisation column since they were declared unscoped. " +
            "They are real tenant data being served unfiltered — remove them from " +
            "OrgQueryFilters.DeclaredUnscoped so the filter applies");
    }

    // ── direction 2: an unjustified IgnoreQueryFilters() ─────────────────────

    private static readonly Regex IgnoreQueryFiltersCall =
        new(@"\.IgnoreQueryFilters\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Call sites permitted to bypass the organisation filter, each with the reason. Empty today:
    /// no production code calls <c>IgnoreQueryFilters()</c>. An empty allowlist is exactly the
    /// condition under which a "the set of violations is empty" assertion is worthless, which is
    /// why <see cref="TheIgnoreQueryFiltersDetector_CatchesASyntheticOffender"/> exists.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedIgnoreQueryFilterSites =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void EveryIgnoreQueryFiltersCall_IsDeclaredWithAReason()
    {
        var repoRoot = OrphanDetector.FindRepoRoot();
        var sources = ProductionSources(repoRoot);

        sources.Count.Should().BeGreaterThan(100,
            $"only {sources.Count} production source file(s) were found under {repoRoot} — the " +
            "scan is broken, not the code");

        var offenders = sources
            .Where(path => IgnoreQueryFiltersCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .Where(rel => !AllowedIgnoreQueryFilterSites.ContainsKey(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "IgnoreQueryFilters() disables organisation scoping for that query. Each use must be " +
            "declared in AllowedIgnoreQueryFilterSites with the reason it is safe, so that " +
            "removing tenant isolation is a decision on the record rather than a one-word edit");
    }

    /// <summary>
    /// The allowlist above is empty and the offender set is empty, so the test that compares them
    /// would pass even if the detector matched nothing at all. This feeds the detector an offender
    /// directly, so a regex that stops working fails here instead of going quietly green.
    /// </summary>
    [Fact]
    public void TheIgnoreQueryFiltersDetector_CatchesASyntheticOffender()
    {
        const string offender = """
            var rows = await _db.PurchaseOrders
                .IgnoreQueryFilters()
                .Where(o => o.Status == "ready")
                .ToListAsync(ct);
            """;

        const string innocent = """
            var rows = await _db.PurchaseOrders
                .Where(o => o.OrgId == orgId && o.Status == "ready")
                .ToListAsync(ct);
            """;

        IgnoreQueryFiltersCall.IsMatch(offender).Should().BeTrue(
            "this is the exact shape the guard exists to catch — a query opting out of the " +
            "organisation filter");

        IgnoreQueryFiltersCall.IsMatch(innocent).Should().BeFalse(
            "an ordinary org-scoped query must not be flagged, or the guard will be disabled for " +
            "noise");
    }

    /// <summary>
    /// Production .cs files only. Test projects are excluded because a test may legitimately
    /// bypass the filter to assert what it hides. <c>.claude/worktrees/</c> holds full copies of
    /// this repo — sibling agents' in-progress code — and is excluded structurally by enumerating
    /// only <c>&lt;root&gt;/&lt;project&gt;</c>, never the repository root itself.
    /// </summary>
    private static IReadOnlyList<string> ProductionSources(string repoRoot) =>
        OrphanDetector.ProductionProjectDirectories(repoRoot)
            .Select(dir => Path.Combine(repoRoot, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
}
