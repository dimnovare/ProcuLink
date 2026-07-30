using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// <b>The orphan guard.</b> "No new surface without a consumer" is a rule the team keeps writing
/// down and keeps shipping past. This file is the mechanism that makes it enforceable.
///
/// <para>An <i>orphan store</i> is a <c>DbSet&lt;T&gt;</c> that the product can write to but that
/// nothing downstream ever reads. It looks alive — there is a table, a service, a controller, a
/// screen, and green CRUD tests — and it is completely inert: rows go in, no behaviour comes out.
/// The user configures something and nothing happens. That failure mode is invisible to CRUD
/// tests, because CRUD tests are exactly the thing that hides it: the entity's own service reads
/// back what its own service wrote.</para>
///
/// <para><b>The rule.</b> Every <c>DbSet&lt;T&gt;</c> declared on <see cref="ProcuLinkDbContext"/>
/// must be READ by at least one production source file that is NOT the entity's own CRUD surface
/// (<c>&lt;Entity&gt;Service.cs</c> / <c>&lt;Entity&gt;sController.cs</c> / …), or appear in
/// <see cref="KnownWriteOnly"/> with a written reason.</para>
///
/// <para><b>The DbSet list is obtained by reflection</b>, never by hand. A hand-maintained list is
/// the same bug in a new place: the orphan you forgot to add to the list is the orphan the guard
/// was supposed to catch. Reflection keys on the property TYPE being <c>DbSet&lt;&gt;</c>, so it
/// covers both declaration styles used in the context (<c>=&gt; Set&lt;T&gt;()</c> expression
/// bodies and <c>{ get; set; } = null!;</c> auto-properties) and any third style added later.</para>
///
/// <para><b>Navigation traversal counts as a read.</b> Plenty of entities are legitimately read
/// only through their parent — <c>.Include(a =&gt; a.Packages).ThenInclude(p =&gt; p.Lines)</c>
/// really does consume <c>AsnPackageLines</c>. Treating those as orphans would bury the real
/// signal under two dozen false positives and the guard would be switched off within a week. So
/// the detector walks the navigation graph to a fixpoint PER FILE, seeded only from DbSets the
/// file actually queries. A navigation is credited only when the traversal token appears in a file
/// that can already reach the declaring entity — which is why a bare property declaration in an
/// entity class (<c>public List&lt;Membership&gt; Memberships { get; set; }</c>) is not a read:
/// declaring a navigation is not consuming it, and the token there has no leading <c>.</c>.</para>
///
/// <para><b>Encapsulation is not orphanhood.</b> A table read only inside its own service is still
/// consumed if that service has a caller beyond the screen that fills it — ParseInvoiceJob injects
/// <c>IInvoiceService</c>, so <c>Invoices</c> drives real behaviour even though <c>_db.Invoices</c>
/// appears in exactly one file. What separates that from an orphan is the caller: for
/// <c>ValidationRules</c> the only caller is ValidationRulesController — the CRUD screen itself.
/// Registration in Program.cs does not count; "registered but dead" is how supplier auto-detect
/// shipped inert.</para>
///
/// <para><b>Known limits, stated honestly.</b> This is a token scan, not a compiler. The receiver
/// must look like a DbContext, so an unrelated <c>BillingFeature.ValidationRules</c> no longer
/// scores as a read — but a navigation name shared by two parents (<c>.Lines</c>) still credits
/// both, and a DbSet reached through an unusually-named field would be missed. Those errors point
/// toward leniency, so a RED here is trustworthy: it means no reader could be found at all. A GREEN
/// is a floor, not a ceiling.</para>
/// </summary>
public class OrphanGuardTests
{
    // ────────────────────────────────────────────────────────────────────────────
    //  Allowlist. An entry here is a PROMISE, not a mute button.
    //
    //  Add an entry only when the store is genuinely consumed by something this
    //  token scan structurally cannot see (a framework, another process, a SQL
    //  view). NEVER add an entry to turn a real orphan green — leave it red and
    //  fix the orphan. `Allowlist_entries_are_not_stale` fails if an entry ever
    //  acquires a real consumer, so this list cannot rot into a permanent muzzle.
    // ────────────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyDictionary<string, string> KnownWriteOnly =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DataProtectionKeys"] =
                "Framework-owned. ProcuLinkDbContext implements IDataProtectionKeyContext purely so "
                + "ASP.NET Core Data Protection can persist its key ring in Postgres. The reader is "
                + "EntityFrameworkCoreXmlRepository inside the framework, not ProcuLink source, so no "
                + "first-party read can ever exist. Not a product surface.",
        };

    /// <summary>Members that MUTATE a DbSet. A DbSet touched only through these is written, not read.</summary>
    private static readonly HashSet<string> WriteMembers = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Remove", "RemoveRange",
        "Update", "UpdateRange",
        "Attach", "AttachRange",
    };

    /// <summary>Projects whose source counts as "production". Test projects deliberately excluded:
    /// a store read only by its own tests is the textbook orphan.</summary>
    private static readonly string[] ProductionProjects =
    {
        "ProcuLink.Api", "ProcuLink.Core", "ProcuLink.Infrastructure",
        "ProcuLink.Transform", "ProcuLink.Worker",
    };

    /// <summary>Suffixes that mark a file as an entity's OWN CRUD surface.</summary>
    private static readonly string[] CrudSuffixes = { "Service", "Controller", "Repository", "Store", "Endpoints" };

    // ═══════════════════════════════════════════════════════════════════════════
    //  THE GUARD
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Every_DbSet_has_a_reader_outside_its_own_crud_surface()
    {
        var model = ReflectModel();
        var files = LoadProductionSources();

        files.Should().NotBeEmpty("the source scan must actually find the repository on disk");
        model.Entities.Should().HaveCountGreaterThan(40, "reflection must see the whole context");

        var orphans = Detector.FindOrphans(model.Entities, model.Navigations, files);
        var unexplained = orphans.Where(o => !KnownWriteOnly.ContainsKey(o.DbSetName)).ToList();

        if (unexplained.Count == 0) return;

        var report = new StringBuilder()
            .AppendLine($"{unexplained.Count} DbSet(s) on ProcuLinkDbContext are written but never read")
            .AppendLine("by any production file outside their own CRUD surface.")
            .AppendLine()
            .AppendLine("Each one is a store the product can fill and nothing consumes: the user")
            .AppendLine("configures it, rows appear, and no behaviour changes. Give it a consumer,")
            .AppendLine("or delete the surface. Do NOT add it to KnownWriteOnly to make this green.")
            .AppendLine();

        foreach (var o in unexplained.OrderBy(o => o.DbSetName, StringComparer.Ordinal))
        {
            report.AppendLine($"  • {o.DbSetName} (entity {o.EntityName})");
            report.AppendLine($"      {o.Reason}");
        }

        Assert.Fail(report.ToString());
    }

    /// <summary>
    /// The allowlist must not outlive its excuse. If an allowlisted store acquires a real consumer,
    /// the entry is now lying about why the guard skips it — delete the entry.
    /// </summary>
    [Fact]
    public void Allowlist_entries_are_not_stale()
    {
        var model = ReflectModel();
        var files = LoadProductionSources();
        var orphanNames = Detector.FindOrphans(model.Entities, model.Navigations, files)
            .Select(o => o.DbSetName).ToHashSet(StringComparer.Ordinal);

        var known = model.Entities.Select(e => e.DbSetName).ToHashSet(StringComparer.Ordinal);

        foreach (var (dbSet, reason) in KnownWriteOnly)
        {
            known.Should().Contain(dbSet,
                $"KnownWriteOnly['{dbSet}'] names a DbSet that no longer exists — delete the entry");
            reason.Trim().Should().NotBeEmpty($"KnownWriteOnly['{dbSet}'] must carry a written reason");
            orphanNames.Should().Contain(dbSet,
                $"KnownWriteOnly['{dbSet}'] is stale: this store now HAS a consumer, so the exemption "
                + "is no longer true. Delete the entry.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  REGRESSION TESTS FOR THE DETECTOR ITSELF
    //
    //  A guard nobody has watched fail is not a guard. These pin the exact defect
    //  class the guard exists to catch, on synthetic inputs, so the detector cannot
    //  silently degrade into an always-green no-op.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Detector_flags_a_synthetic_orphan_store()
    {
        var entities = new[]
        {
            new EntityNode("Widget", "Widgets"),
            new EntityNode("OrphanProbe", "OrphanProbes"),
        };
        var files = new[]
        {
            // OrphanProbe has a full, working, tested CRUD service — and no consumer.
            new SourceFile("ProcuLink.Infrastructure/Services/OrphanProbeService.cs",
                "var all = await _db.OrphanProbes.Where(x => x.OrgId == orgId).ToListAsync();\n"
                + "_db.OrphanProbes.Add(probe);"),
            new SourceFile("ProcuLink.Api/Controllers/OrphanProbesController.cs",
                "return Ok(await _db.OrphanProbes.ToListAsync());"),
            new SourceFile("ProcuLink.Infrastructure/Services/PricingService.cs",
                "var w = await _db.Widgets.FirstOrDefaultAsync(x => x.Id == id);"),
        };

        var orphans = Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files);

        orphans.Select(o => o.DbSetName).Should().BeEquivalentTo(new[] { "OrphanProbes" });
        orphans.Single().Reason.Should().Contain("OrphanProbeService.cs");
    }

    [Fact]
    public void Detector_accepts_a_read_from_outside_the_crud_surface()
    {
        var entities = new[] { new EntityNode("Widget", "Widgets") };
        var files = new[]
        {
            new SourceFile("ProcuLink.Infrastructure/Services/WidgetService.cs", "_db.Widgets.Add(w);"),
            new SourceFile("ProcuLink.Infrastructure/Services/PricingService.cs",
                "var w = await _db.Widgets.FirstOrDefaultAsync(x => x.Id == id);"),
        };

        Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files).Should().BeEmpty();
    }

    [Fact]
    public void Detector_does_not_count_a_write_as_a_read()
    {
        var entities = new[] { new EntityNode("Widget", "Widgets") };
        var files = new[]
        {
            // Three different writers, none of which ever queries it back.
            new SourceFile("ProcuLink.Api/Controllers/OrdersController.cs", "_db.Widgets.Add(w);"),
            new SourceFile("ProcuLink.Worker/Jobs/SweepJob.cs", "_db.Widgets.RemoveRange(stale);"),
            new SourceFile("ProcuLink.Infrastructure/Services/SyncService.cs", "_db.Widgets.UpdateRange(batch);"),
        };

        Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files)
            .Select(o => o.DbSetName).Should().BeEquivalentTo(new[] { "Widgets" });
    }

    [Fact]
    public void Detector_credits_navigation_traversal_from_a_reachable_root()
    {
        // The DESADV shape: Asn is queried by DbSet, Packages and Lines only through Include.
        var entities = new[]
        {
            new EntityNode("Asn", "AdvanceShippingNotices"),
            new EntityNode("AsnPackage", "AsnPackages"),
            new EntityNode("AsnPackageLine", "AsnPackageLines"),
        };
        var navs = new[]
        {
            new NavEdge("Asn", "Packages", "AsnPackage"),
            new NavEdge("AsnPackage", "Lines", "AsnPackageLine"),
        };
        var files = new[]
        {
            new SourceFile("ProcuLink.Infrastructure/Services/DesadvService.cs",
                "await _db.AdvanceShippingNotices\n"
                + "    .Include(a => a.Packages).ThenInclude(p => p.Lines)\n"
                + "    .FirstOrDefaultAsync(ct);"),
        };

        Detector.FindOrphans(entities, navs, files).Should().BeEmpty(
            "an entity consumed two navigation hops deep is consumed");
    }

    [Fact]
    public void Detector_does_not_credit_a_navigation_the_file_cannot_reach()
    {
        // The Memberships shape: the navigation is DECLARED on an entity class, but no file
        // queries the parent DbSet and then traverses it. Declaring a navigation is not a read.
        var entities = new[]
        {
            new EntityNode("AppUser", "Users"),
            new EntityNode("Membership", "Memberships"),
        };
        var navs = new[] { new NavEdge("AppUser", "Memberships", "Membership") };
        var files = new[]
        {
            new SourceFile("ProcuLink.Core/Entities/AppUser.cs",
                "public List<Membership> Memberships { get; set; } = new();"),
            new SourceFile("ProcuLink.Infrastructure/Services/UserSyncService.cs",
                "var u = await _db.Users.FirstOrDefaultAsync(x => x.ClerkUserId == id);"),
        };

        Detector.FindOrphans(entities, navs, files)
            .Select(o => o.DbSetName).Should().BeEquivalentTo(new[] { "Memberships" });
    }

    /// <summary>
    /// Encapsulation is not orphanhood. The ParseInvoiceJob shape: nothing outside InvoiceService.cs
    /// ever touches <c>_db.Invoices</c>, but a background job depends on <c>IInvoiceService</c>, so
    /// the rows drive real behaviour.
    /// </summary>
    [Fact]
    public void Detector_credits_consumption_delegated_through_the_owning_service()
    {
        var entities = new[] { new EntityNode("InvoiceEntity", "Invoices") };
        var files = new[]
        {
            new SourceFile("ProcuLink.Core/Services/IInvoiceService.cs", "public interface IInvoiceService { }"),
            new SourceFile("ProcuLink.Infrastructure/Services/InvoiceService.cs",
                "var inv = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(ct);"),
            new SourceFile("ProcuLink.Api/Controllers/InvoiceController.cs", "private readonly IInvoiceService _svc;"),
            new SourceFile("ProcuLink.Api/Jobs/ParseInvoiceJob.cs", "public ParseInvoiceJob(IInvoiceService svc) { }"),
        };

        Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files).Should().BeEmpty(
            "a background job depends on the owning service, so the table drives behaviour");
    }

    /// <summary>
    /// The ValidationRule shape, and the whole point of the packet: a complete, working, tested CRUD
    /// stack whose only caller is its own configuration screen. Registering the service in Program.cs
    /// is not consumption — "registered but dead" is exactly how surfaces ship inert.
    /// </summary>
    [Fact]
    public void Detector_does_not_credit_the_crud_screen_or_the_DI_registration_as_consumers()
    {
        var entities = new[] { new EntityNode("ValidationRule", "ValidationRules") };
        var files = new[]
        {
            new SourceFile("ProcuLink.Core/Services/IValidationRuleService.cs", "public interface IValidationRuleService { }"),
            new SourceFile("ProcuLink.Infrastructure/Services/ValidationRuleService.cs",
                "return await _db.ValidationRules.ToListAsync();"),
            new SourceFile("ProcuLink.Api/Controllers/ValidationRulesController.cs",
                "public ValidationRulesController(IValidationRuleService svc) { }"),
            new SourceFile("ProcuLink.Api/Program.cs",
                "builder.Services.AddScoped<IValidationRuleService, ValidationRuleService>();"),
        };

        Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files)
            .Select(o => o.DbSetName).Should().BeEquivalentTo(new[] { "ValidationRules" },
                "rules the user can create, list and edit — and that no evaluator ever reads — are inert");
    }

    /// <summary>
    /// Regression: the first draft of this guard went GREEN on the ValidationRule orphan because
    /// <c>BillingFeature.ValidationRules</c> — an unrelated enum member in PlanConstants.cs — matched
    /// a bare `.ValidationRules` scan and was scored as a read. A guard that can be silenced by an
    /// enum that happens to share a name is not a guard.
    /// </summary>
    [Fact]
    public void Detector_ignores_an_unrelated_member_that_shares_a_DbSet_name()
    {
        var entities = new[] { new EntityNode("ValidationRule", "ValidationRules") };
        var files = new[]
        {
            new SourceFile("ProcuLink.Infrastructure/Services/ValidationRuleService.cs",
                "return await _db.ValidationRules.Where(r => r.OrgId == orgId).ToListAsync();"),
            new SourceFile("ProcuLink.Core/Constants/PlanConstants.cs",
                "[BillingFeature.ValidationRules]    = Growth,"),
            new SourceFile("ProcuLink.Core/Constants/BillingFeature.cs", "    ValidationRules,"),
        };

        Detector.FindOrphans(entities, Array.Empty<NavEdge>(), files)
            .Select(o => o.DbSetName).Should().BeEquivalentTo(new[] { "ValidationRules" },
                "an enum member is not a consumer of the table");
    }

    [Fact]
    public void Reflection_sees_both_DbSet_declaration_styles_on_the_real_context()
    {
        var names = ReflectModel().Entities.Select(e => e.DbSetName).ToList();

        // `public DbSet<Organisation> Organisations => Set<Organisation>();`  (expression-bodied)
        names.Should().Contain("Organisations");
        // `public DbSet<RuleDefinition> RuleDefinitions { get; set; } = null!;` (auto-property)
        names.Should().Contain("RuleDefinitions");
        names.Should().OnlyHaveUniqueItems();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MODEL REFLECTION
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed record ReflectedModel(IReadOnlyList<EntityNode> Entities, IReadOnlyList<NavEdge> Navigations);

    private static ReflectedModel ReflectModel()
    {
        // Key on the PROPERTY TYPE, not on a source regex: declaration style is irrelevant.
        var dbSets = typeof(ProcuLinkDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => (Prop: p, Clr: p.PropertyType.GetGenericArguments()[0]))
            .ToList();

        var entities = dbSets.Select(x => new EntityNode(x.Clr.Name, x.Prop.Name)).ToList();
        var clrByName = dbSets.ToDictionary(x => x.Clr.Name, x => x.Clr, StringComparer.Ordinal);

        var navs = new List<NavEdge>();
        foreach (var (name, clr) in clrByName)
        {
            foreach (var prop in clr.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var target = NavigationTarget(prop.PropertyType);
                if (target is null || !clrByName.ContainsKey(target.Name)) continue;
                navs.Add(new NavEdge(name, prop.Name, target.Name));
            }
        }

        return new ReflectedModel(entities, navs);
    }

    /// <summary>Resolves T for a reference navigation (T) or a collection navigation (IEnumerable&lt;T&gt;).</summary>
    private static Type? NavigationTarget(Type propertyType)
    {
        if (propertyType.IsPrimitive || propertyType == typeof(string) || propertyType.IsEnum) return null;

        if (propertyType.IsGenericType)
        {
            var arg = propertyType.GetGenericArguments().FirstOrDefault();
            if (arg is not null
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
                return arg;
            return null;
        }

        return propertyType.IsClass ? propertyType : null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SOURCE CORPUS
    // ═══════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<SourceFile> LoadProductionSources()
    {
        var root = RepositoryRoot();
        var files = new List<SourceFile>();

        foreach (var project in ProductionProjects)
        {
            var dir = Path.Combine(root, project);
            if (!Directory.Exists(dir)) continue;

            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');

                if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                // EF scaffolding restates the whole model; crediting it would green-light everything.
                if (rel.Contains("/Migrations/", StringComparison.Ordinal)) continue;
                // The context itself only DECLARES the sets and configures the model. A declaration
                // and a `.WithMany(x => x.Memberships)` mapping are not consumption.
                if (Path.GetFileName(path).StartsWith("ProcuLinkDbContext", StringComparison.Ordinal)) continue;

                files.Add(new SourceFile(rel, File.ReadAllText(path)));
            }
        }

        return files;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate ProcuLink.slnx above " + AppContext.BaseDirectory);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DETECTOR (pure — no I/O, no reflection; unit-testable above)
    // ═══════════════════════════════════════════════════════════════════════════

    internal sealed record EntityNode(string EntityName, string DbSetName);

    internal sealed record NavEdge(string FromEntity, string NavName, string ToEntity);

    internal sealed record SourceFile(string RelativePath, string Text);

    internal sealed record Orphan(string DbSetName, string EntityName, string Reason);

    internal static class Detector
    {
        public static IReadOnlyList<Orphan> FindOrphans(
            IReadOnlyList<EntityNode> entities,
            IReadOnlyList<NavEdge> navigations,
            IReadOnlyList<SourceFile> files)
        {
            // Per file: which entities does this file actually consume?
            var reachablePerFile = files
                .ToDictionary(f => f.RelativePath, f => Reachable(f.Text, entities, navigations), StringComparer.Ordinal);

            var orphans = new List<Orphan>();

            foreach (var entity in entities)
            {
                var readers = files
                    .Where(f => reachablePerFile[f.RelativePath].Contains(entity.EntityName))
                    .ToList();

                var consumers = readers.Where(f => !IsOwnCrudSurface(f.RelativePath, entity)).ToList();
                if (consumers.Count > 0) continue;

                // Second chance: the table may be encapsulated behind its own service, with the
                // real consumption one hop away (ParseInvoiceJob injects IInvoiceService; nothing
                // outside InvoiceService.cs ever touches _db.Invoices). That is a consumed store.
                if (IsDelegatedToAConsumer(entity, files)) continue;

                orphans.Add(new Orphan(entity.DbSetName, entity.EntityName, Explain(entity, readers, files)));
            }

            return orphans;
        }

        private static string Explain(EntityNode entity, IReadOnlyList<SourceFile> readers, IReadOnlyList<SourceFile> files)
        {
            if (readers.Count > 0)
            {
                var where = string.Join(", ", readers.Select(r => Path.GetFileName(r.RelativePath)).Distinct().Order());
                return $"read ONLY by its own CRUD surface ({where}). Its own service reading back what "
                     + "its own service wrote is not a consumer — nothing downstream acts on this data.";
            }

            var writers = files
                .Where(f => Regex.IsMatch(
                    f.Text,
                    $@"[A-Za-z_][A-Za-z0-9_]*\s*\.\s*{Regex.Escape(entity.DbSetName)}\s*\.\s*(?:{string.Join("|", WriteMembers)})\b"))
                .Select(f => Path.GetFileName(f.RelativePath)).Distinct().Order().ToList();

            return writers.Count > 0
                ? $"never read anywhere in production source; written by {string.Join(", ", writers)} "
                  + "and consumed by nothing."
                : "no production file queries it and no production file traverses into it — "
                  + "declared on the context and otherwise dead.";
        }

        /// <summary>
        /// Entities this file consumes: DbSets it queries, plus everything reachable from those by
        /// navigation traversals the file actually performs. Fixpoint, so multi-hop
        /// <c>.Include(...).ThenInclude(...)</c> chains resolve.
        /// </summary>
        private static HashSet<string> Reachable(
            string text, IReadOnlyList<EntityNode> entities, IReadOnlyList<NavEdge> navigations)
        {
            var reachable = entities
                .Where(e => QueriesDbSet(text, e.DbSetName))
                .Select(e => e.EntityName)
                .ToHashSet(StringComparer.Ordinal);

            bool grew;
            do
            {
                grew = false;
                foreach (var edge in navigations)
                {
                    if (!reachable.Contains(edge.FromEntity) || reachable.Contains(edge.ToEntity)) continue;
                    if (!Traverses(text, edge.NavName)) continue;
                    reachable.Add(edge.ToEntity);
                    grew = true;
                }
            } while (grew);

            return reachable;
        }

        /// <summary>
        /// True when the file uses the DbSet for anything other than pure mutation.
        ///
        /// <para>The receiver must look like a DbContext (<c>_db</c>, <c>db</c>, <c>_context</c>,
        /// <c>ctx</c>, …). Without that check an unrelated member sharing the set's name scores a
        /// read and the guard goes green on a real orphan — which is not hypothetical:
        /// <c>BillingFeature.ValidationRules</c> in PlanConstants.cs matched a naive
        /// <c>.ValidationRules</c> scan and single-handedly hid the ValidationRule orphan.</para>
        /// </summary>
        private static bool QueriesDbSet(string text, string dbSetName)
        {
            // `_db.Foos` / `db.Foos` / `_context.Foos`, plus `…GetRequiredService<ProcuLinkDbContext>().Foos`.
            var pattern = $@"(?:(?<recv>[A-Za-z_][A-Za-z0-9_]*)|(?<call>\)))\s*\.\s*{Regex.Escape(dbSetName)}\b";

            foreach (Match hit in Regex.Matches(text, pattern))
            {
                if (hit.Groups["recv"].Success && !LooksLikeDbContext(hit.Groups["recv"].Value)) continue;
                if (hit.Groups["call"].Success && !MentionsDbContextNearby(text, hit.Index)) continue;

                var next = Regex.Match(text[(hit.Index + hit.Length)..], @"^\s*\.\s*(\w+)");

                // No trailing member: the set is handed around as an IQueryable
                // (`from x in db.Foo`, `Project(db.Foo)`) — that is a read.
                if (!next.Success) return true;
                if (!WriteMembers.Contains(next.Groups[1].Value)) return true;
            }

            return false;
        }

        private static bool LooksLikeDbContext(string receiver)
            => Regex.IsMatch(receiver, @"^_?[A-Za-z0-9]*(?:[Dd]b|[Cc]ontext|[Cc]tx)$");

        private static bool MentionsDbContextNearby(string text, int index)
        {
            var from = Math.Max(0, index - 120);
            return text[from..index].Contains("DbContext", StringComparison.Ordinal);
        }

        /// <summary>
        /// A traversal is <c>.Nav</c> with a leading dot. A bare <c>public List&lt;T&gt; Nav { get; set; }</c>
        /// declaration has no leading dot, so declaring a navigation never counts as using it.
        /// </summary>
        private static bool Traverses(string text, string navName)
            => Regex.IsMatch(text, $@"\.\s*{Regex.Escape(navName)}\b");

        /// <summary>
        /// True when the entity's own service/repository is INJECTED somewhere that is neither its
        /// own CRUD surface nor the DI composition root.
        ///
        /// <para>Encapsulation is not orphanhood. <c>_db.Invoices</c> appears only inside
        /// InvoiceService.cs, but ParseInvoiceJob depends on <c>IInvoiceService</c> — the rows drive
        /// real pipeline behaviour and the table is not an orphan. The distinction that matters is
        /// whether the owning service has any caller beyond the screen that fills the table:
        /// <c>IValidationRuleService</c> is injected by exactly one file, ValidationRulesController,
        /// which is the CRUD screen itself. Written, listed, edited, and evaluated by nothing.</para>
        ///
        /// <para>Program.cs is excluded deliberately: registering a service in the container is not
        /// using it. "Registered but dead" is precisely how supplier auto-detect shipped inert.</para>
        /// </summary>
        private static bool IsDelegatedToAConsumer(EntityNode entity, IReadOnlyList<SourceFile> files)
        {
            var surfaceTypes = files
                .Where(f => IsOwnCrudSurface(f.RelativePath, entity))
                .Select(f => Path.GetFileNameWithoutExtension(f.RelativePath))
                .SelectMany(n => new[] { StripInterfacePrefix(n), "I" + StripInterfacePrefix(n) })
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (surfaceTypes.Count == 0) return false;

            return files.Any(f =>
                !IsOwnCrudSurface(f.RelativePath, entity)
                && !IsCompositionRoot(f.RelativePath)
                && surfaceTypes.Any(t => Regex.IsMatch(f.Text, $@"\b{Regex.Escape(t)}\b")));
        }

        private static bool IsCompositionRoot(string relativePath)
            => Path.GetFileName(relativePath) is "Program.cs" or "Startup.cs";

        private static string StripInterfacePrefix(string name)
            => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

        /// <summary>
        /// The entity's own CRUD surface — the files that exist solely to persist and read back
        /// this one entity. A read here proves the table round-trips, not that anything consumes it.
        /// Covers the interface declaration too (<c>IOutputTemplateService.cs</c>), otherwise the
        /// contract file itself scores as a consumer of its own implementation.
        /// </summary>
        private static bool IsOwnCrudSurface(string relativePath, EntityNode entity)
        {
            var name = StripInterfacePrefix(Path.GetFileNameWithoutExtension(relativePath));

            foreach (var suffix in CrudSuffixes)
            {
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                if (NamesTheEntity(name[..^suffix.Length], entity)) return true;
            }

            return false;
        }

        private static bool NamesTheEntity(string stem, EntityNode entity)
        {
            var bare = entity.EntityName.EndsWith("Entity", StringComparison.Ordinal)
                ? entity.EntityName[..^"Entity".Length]
                : entity.EntityName;

            return Same(stem, entity.EntityName)
                || Same(stem, entity.DbSetName)
                || Same(stem, bare)
                || Same(stem, bare + "s")
                || Same(stem, bare + "es");
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
