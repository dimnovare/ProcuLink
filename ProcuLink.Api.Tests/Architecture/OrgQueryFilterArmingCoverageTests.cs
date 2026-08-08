using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Controllers;
using ProcuLink.Infrastructure.Tenancy;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Structural guards for ARMING the organisation query filters on the request path.
///
/// <para>PR #178 shipped the filters inert; this pins the two ways arming goes wrong afterwards:</para>
/// <list type="number">
///   <item><description>A cross-organisation endpoint loses its opt-out, or a new one is declared
///   without review — caught by <see cref="EveryCrossOrganisationEndpoint_IsDeclaredWithAReason"/>,
///   which pins the whole inventory rather than counting it.</description></item>
///   <item><description>Arming spreads to a second site, or into a host with no HTTP request —
///   caught by <see cref="TheOnlySiteThatArmsTheFilters_IsTenantResolutionMiddleware"/> and
///   <see cref="NoBackgroundHostArmsTheFilters"/>. A Worker sweep that armed itself would match zero
///   rows and log a successful run.</description></item>
/// </list>
///
/// <para>Both scans carry anti-vacuity floors on what was DETECTED, not merely on how many files
/// were read. A file-count floor is not a detection floor: a scan repointed at a project with no
/// matches passes the file count while proving nothing.</para>
/// </summary>
public sealed class OrgQueryFilterArmingCoverageTests
{
    // ── direction 1: the cross-organisation opt-out inventory ────────────────

    /// <summary>
    /// Every endpoint permitted to read across organisations, pinned by name.
    ///
    /// <para>This is a pin, not a floor. Adding an entry is a deliberate, reviewed act — because an
    /// opt-out does not fail loudly. It makes the endpoint answer 200 with another organisation's
    /// data folded in, or (when removed) with the caller's own slice reported as the whole system.
    /// Neither shows up as an error anywhere.</para>
    ///
    /// <para>Key is <c>Type.Name</c> for a class-wide opt-out, or <c>Type.Name.MethodName</c> for a
    /// single action.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExpectedCrossOrganisationEndpoints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AdminController"] =
                "The platform-owner surface. Every action aggregates or acts across all " +
                "organisations; scoping it would report one tenant's numbers as the platform's, and " +
                "would make admin data erasure silently erase nothing. Gated by [AdminOnly].",

            ["BillingController.Webhook"] =
                "Stripe names the organisation in the event payload, never the caller. Unauthenticated " +
                "in practice, so the reads are unfiltered by accident; declared so it is deliberate.",

            ["InboundEmailController.Postmark"] =
                "The organisation is derived from the recipient address, never the caller. " +
                "Token-authenticated rather than scheme-authenticated, so no tenant resolves today.",
        };

    /// <summary>
    /// Detection floor. Three opt-outs exist; if the reflection ever stops finding them, "the
    /// unexpected set is empty" stays technically true while protecting nothing.
    /// </summary>
    private const int MinimumCrossOrganisationEndpointsDetected = 3;

    /// <summary>Corpus floor — 37 controllers today.</summary>
    private const int MinimumControllersScanned = 30;

    [Fact]
    public void EveryCrossOrganisationEndpoint_IsDeclaredWithAReason()
    {
        var controllers = Controllers();

        controllers.Count.Should().BeGreaterThanOrEqualTo(MinimumControllersScanned,
            $"only {controllers.Count} controller(s) were found in the API assembly — the scan is " +
            "broken, not the code");

        var found = DiscoverCrossOrganisationEndpoints(controllers);

        found.Count.Should().BeGreaterThanOrEqualTo(MinimumCrossOrganisationEndpointsDetected,
            $"only {found.Count} cross-organisation endpoint(s) were DETECTED. The inventory " +
            "comparison below passes trivially when nothing is detected at all, so this floor is " +
            "what fails when the attribute is renamed, moved, or stops being discoverable by " +
            "reflection");

        found.Keys.Should().BeEquivalentTo(ExpectedCrossOrganisationEndpoints.Keys,
            "a cross-organisation endpoint is one the request pipeline deliberately does NOT scope. " +
            "Adding one silently widens what a request can read; removing one silently truncates a " +
            "cross-tenant view to the caller's own organisation, and both come back 200. The set " +
            "must therefore be reviewed, not discovered");

        foreach (var (endpoint, reason) in found)
        {
            reason.Should().NotBeNullOrWhiteSpace(
                $"{endpoint} opts out of organisation scoping and must say why");

            reason!.Length.Should().BeGreaterThan(40,
                $"{endpoint} states '{reason}', which is too short to be a reason anyone can review");
        }
    }

    /// <summary>
    /// The inventory test compares two sets that are populated by the same reflection walk, so a
    /// walk that found nothing would still have to fail the detection floor to be caught. This
    /// proves the walk itself reads the attribute off a real declaration — the class-wide opt-out on
    /// <see cref="AdminController"/> — and that an ordinary controller is not flagged.
    /// </summary>
    [Fact]
    public void TheCrossOrganisationDetector_ReadsARealDeclaration_AndLeavesOrdinaryControllersAlone()
    {
        var onTheAdminSurface = typeof(AdminController)
            .GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true);

        onTheAdminSurface.Should().NotBeNull(
            "AdminController is the one deliberately cross-tenant controller; if this attribute is " +
            "gone, every admin read is silently restricted to the signed-in admin's own organisation");

        onTheAdminSurface!.Reason.Should().NotBeNullOrWhiteSpace();

        typeof(OrdersController)
            .GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true)
            .Should().BeNull(
                "OrdersController is tenant-facing and must stay armed. A detector that flagged it " +
                "would be matching something other than the declaration");
    }

    /// <summary>The attribute refuses to exist without a reason, so the inventory can never hold a blank one.</summary>
    [Fact]
    public void ACrossOrganisationDeclarationWithoutAReason_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new CrossOrganisationReadAttribute("   "));
    }

    // ── direction 2: arming spreading beyond the one request-path site ───────

    private static readonly Regex ArmingCall =
        new(@"\.ScopeToOrganisation\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The single production site permitted to arm the filters. Arming is per-DbContext and must
    /// happen before that context's first query, so a second site is not additive — it is either
    /// dead or a crash.
    /// </summary>
    private const string TheArmingSite = "ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs";

    [Fact]
    public void TheOnlySiteThatArmsTheFilters_IsTenantResolutionMiddleware()
    {
        var repoRoot = OrphanDetector.FindRepoRoot();
        var sources = ProductionSources(repoRoot);

        sources.Count.Should().BeGreaterThan(100,
            $"only {sources.Count} production source file(s) were found under {repoRoot} — the scan " +
            "is broken, not the code");

        var armingSites = sources
            .Where(path => ArmingCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();

        armingSites.Should().BeEquivalentTo([TheArmingSite],
            "organisation scoping is armed once, on the request path, where the resolved tenant is " +
            "known and the context has not yet been queried. A site outside that file either arms a " +
            "context that has already run a query (which throws) or arms one in a host with no HTTP " +
            "request — and in a background sweep that means matching zero rows and reporting a clean run");
    }

    /// <summary>
    /// The Worker runs cross-organisation sweeps — billing reconciliation, stuck/stranded/SLA
    /// detection, IMAP/SFTP/S3 polling, retention, alerting — on contexts that must stay unscoped.
    /// It never registers <c>ICurrentTenantService</c> and has no HTTP request to resolve a tenant
    /// from, so arming there could only ever be a mistake, and its symptom is silence.
    /// </summary>
    [Fact]
    public void NoBackgroundHostArmsTheFilters()
    {
        var repoRoot = OrphanDetector.FindRepoRoot();

        var backgroundSources = ProductionSources(repoRoot)
            .Where(path =>
                Rel(repoRoot, path).StartsWith("ProcuLink.Worker/", StringComparison.Ordinal) ||
                Rel(repoRoot, path).StartsWith("ProcuLink.Infrastructure/", StringComparison.Ordinal))
            .ToList();

        backgroundSources.Count.Should().BeGreaterThan(50,
            $"only {backgroundSources.Count} Worker/Infrastructure source file(s) were found — the " +
            "scan is pointed at the wrong place, so it would pass no matter what those projects contain");

        backgroundSources
            .Where(path => ArmingCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Rel(repoRoot, path))
            .Should().BeEmpty(
                "a background sweep that scoped itself to one organisation would return that " +
                "organisation's rows (or none) and log a successful run over them — the failure mode " +
                "that pages nobody");
    }

    /// <summary>
    /// Both scans above assert an empty or single-element result, which is exactly the condition
    /// under which a regex that matches nothing looks correct. This feeds the detector a known
    /// offender and a known innocent so a broken pattern fails here instead of going quietly green.
    /// </summary>
    [Fact]
    public void TheArmingDetector_CatchesASyntheticOffender()
    {
        const string offender = """
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.ScopeToOrganisation(orgId);
            """;

        const string innocent = """
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.UseCrossOrganisationScope("billing reconciliation sweeps every organisation");
            """;

        ArmingCall.IsMatch(offender).Should().BeTrue(
            "this is the exact shape the guard exists to catch — a second site arming the filters");

        ArmingCall.IsMatch(innocent).Should().BeFalse(
            "declaring a cross-organisation scope is the opposite of arming and must not be flagged, " +
            "or the guard will be disabled for noise");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Rel(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private static IReadOnlyList<Type> Controllers() =>
        typeof(AdminController).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Walks controllers and their public actions for <see cref="CrossOrganisationReadAttribute"/>.
    /// A class-wide declaration is reported once, under the type name, rather than once per action —
    /// otherwise adding an action to the admin surface would churn the pinned inventory and train
    /// reviewers to re-baseline it without looking.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> DiscoverCrossOrganisationEndpoints(
        IReadOnlyList<Type> controllers)
    {
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var controller in controllers)
        {
            var onClass = controller.GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true);
            if (onClass is not null)
            {
                found[controller.Name] = onClass.Reason;
                continue;
            }

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var onAction = action.GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true);
                if (onAction is not null)
                    found[$"{controller.Name}.{action.Name}"] = onAction.Reason;
            }
        }

        return found;
    }

    /// <summary>
    /// Production .cs only. Test projects are excluded — a test may legitimately arm a context to
    /// assert what arming does. <c>.claude/worktrees/</c> holds full copies of this repo (sibling
    /// agents' in-progress code) and is excluded structurally by enumerating only
    /// <c>&lt;root&gt;/&lt;project&gt;</c>, never the repository root itself.
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
