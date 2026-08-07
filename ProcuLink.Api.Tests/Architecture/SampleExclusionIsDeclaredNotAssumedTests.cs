using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WHICH DIRECTION THE NEXT INSTANCE COMES FROM.
///
/// <para>v2 audit P0-5 was one count over one population disagreeing with one list over another:
/// <c>GET /api/orders/summary</c> filtered <c>!o.IsSample</c>, <c>OrderQueryService</c>'s base query
/// did not, and a first-run org read "Received 0" beside a table listing its practice order.
/// <see cref="ProcuLink.Api.Tests.Controllers.OrderCountsAndListDescribeOnePopulationTests"/> pins
/// that behaviour. It cannot pin the NEXT one, because the next one will be a THIRD count, in a
/// file that does not exist yet, rendered beside the other two.</para>
///
/// <para><b>Why nothing else would catch it.</b> There is no EF <c>HasQueryFilter</c> on
/// <c>IsSample</c> (grep <c>ProcuLinkDbContext.cs:450</c> — the property is mapped, never
/// filtered). The exclusion is a CONVENTION repeated by hand at every call site, so a new
/// aggregate that forgets it is silently wrong and no compiler, analyzer or runtime check
/// notices. That is exactly how the list came to disagree in the first place.</para>
///
/// <para><b>What this test does.</b> It finds every aggregate (<c>CountAsync</c> /
/// <c>LongCountAsync</c> / <c>GroupBy</c>) over the <c>PurchaseOrders</c> DbSet in production code
/// and requires each one to DECLARE which population it counts — either in the query itself, by
/// naming <c>IsSample</c>, or in <see cref="SampleInclusiveByDesign"/> below with a reason. A new
/// aggregate anywhere fails the build until its author makes that choice consciously.</para>
///
/// <para><b>It is deliberately bidirectional.</b> The registry pins an exact COUNT per file, not a
/// mere "this file is exempt". Adding a sample-inclusive aggregate to an already-listed file fails
/// (the count goes up); teaching one of them to exclude samples also fails (the count goes down),
/// so the registry cannot rot into a stale exemption list that quietly covers new code.</para>
///
/// <para><b>This is a census, not a verdict.</b> Being listed here does not mean a site is correct
/// — several entries are known gaps recorded on purpose, with the consequence spelled out. The
/// point is that the population each query describes is written down somewhere a reviewer can
/// read, instead of being inferred from a missing predicate.</para>
/// </summary>
public class SampleExclusionIsDeclaredNotAssumedTests
{
    /// <summary>
    /// Aggregates over <c>PurchaseOrders</c> that deliberately COUNT practice orders, keyed by
    /// repo-relative path, with the exact number of such sites in each file.
    ///
    /// <para>To add an entry you must say what the number means for a user who ran the practice
    /// flow. "It didn't seem to matter" is not a reason — that sentence is the defect.</para>
    /// </summary>
    private static readonly Dictionary<string, (int Sites, string Why)> SampleInclusiveByDesign = new()
    {
        ["ProcuLink.Api/Controllers/OrdersController.cs"] =
            (1,
             "GET /api/orders/dead-letter-count. Counts work NEEDING ATTENTION, not volume " +
             "processed, so it is not one of the numbers that has to reconcile with the billing " +
             "meter. A practice order that dead-lettered is a genuinely broken thing on the " +
             "user's screen; excluding it would hide a real delivery failure."),

        ["ProcuLink.Infrastructure/Services/OpsHealthService.cs"] =
            (6,
             "GET /api/ops/health (4 org-scoped: the status GROUP BY, parsing-stuck, " +
             "delivering-stuck, SLA-breached) plus 2 deliberately CROSS-TENANT worker probes " +
             "that are not org-scoped at all. Same reasoning as dead-letter-count: these count " +
             "problems, not throughput. KNOWN CONSEQUENCE, recorded rather than hidden — a stuck " +
             "or failed practice order does appear on operations health and does break the " +
             "'All clear' gate. That is arguably correct (the job really is stuck) but it has " +
             "not been decided; it is not the P0-5 defect, which was a VOLUME count."),

        ["ProcuLink.Infrastructure/Services/BuyerService.cs"] =
            (1,
             "GET /api/buyers per-buyer orderCount. KNOWN GAP, not a justification. The practice " +
             "order carries the fixture's buyer name once parsed, so a first-run org sees a buyer " +
             "it never traded with, carrying one order. This is the same shape as the wire " +
             "topology, which WAS fixed (DashboardController.cs:140). Left alone here only " +
             "because it is a different screen from the P0-5 pairing and is tracked separately."),

        ["ProcuLink.Infrastructure/Services/Detection/SupplierSuggestionService.cs"] =
            (1,
             "Sender-domain → supplier routing history. UNREACHABLE for practice orders rather " +
             "than exempt: the predicate requires o.InboundSenderDomain == senderDomain, and " +
             "SampleOrderService never assigns InboundSenderDomain (verified — the field does not " +
             "appear in that file), so a sample can never join this aggregate. Filtering IsSample " +
             "here would be dead code."),
    };

    /// <summary>
    /// Anti-vacuity floor. Every assertion in this file is "the set of undeclared sites is empty",
    /// which a scanner that matches NOTHING satisfies perfectly. If a refactor renames the DbSet,
    /// moves the projects, or breaks the statement splitter, this floor fails loudly instead of
    /// letting the whole guard pass for free. Raise it when the codebase genuinely grows; never
    /// lower it to make a red build green.
    /// </summary>
    private const int MinimumAggregateSitesExpected = 12;

    /// <summary>Companion floor: the convention must be visibly PRESENT, not merely unviolated.</summary>
    private const int MinimumSampleAwareSitesExpected = 8;

    [Fact]
    public void EveryAggregateOverPurchaseOrders_DeclaresWhichPopulationItCounts()
    {
        var (sampleAware, sampleInclusive) = ScanProductionSources();

        // ── Anti-vacuity ─────────────────────────────────────────────────────
        var totalFound = sampleAware.Count + sampleInclusive.Count;
        totalFound.Should().BeGreaterThanOrEqualTo(MinimumAggregateSitesExpected,
            $"the scanner found only {totalFound} aggregate(s) over PurchaseOrders in production " +
            "code, which is fewer than this codebase is known to contain — the scan itself is " +
            "broken, and every assertion below is passing over an empty set");

        sampleAware.Count.Should().BeGreaterThanOrEqualTo(MinimumSampleAwareSitesExpected,
            $"only {sampleAware.Count} aggregate(s) mention IsSample; the exclusion convention " +
            "should be visible at many more sites, so the IsSample detection is probably broken");

        // ── The registry must describe reality, exactly ──────────────────────
        var actualByFile = sampleInclusive
            .GroupBy(s => s.File)
            .ToDictionary(g => g.Key, g => g.ToList());

        var undeclared = actualByFile
            .Where(kv => !SampleInclusiveByDesign.ContainsKey(kv.Key))
            .SelectMany(kv => kv.Value)
            .OrderBy(s => s.File).ThenBy(s => s.Line)
            .ToList();

        undeclared.Should().BeEmpty(
            "every aggregate over PurchaseOrders must declare which population it counts.\n" +
            "These count orders WITHOUT excluding practice orders, and are not in the registry:\n" +
            $"{Render(undeclared)}\n" +
            "Either add `!o.IsSample` to the query — matching the billing meter " +
            "(StripeBillingService.CountOrdersAsync), the dashboard KPIs and the onboarding " +
            "milestones — or add the file to SampleInclusiveByDesign with a reason stating what " +
            "the number means for an org that ran the practice flow.\n");

        foreach (var (file, (declaredSites, why)) in SampleInclusiveByDesign)
        {
            var actual = actualByFile.TryGetValue(file, out var found) ? found : new List<Site>();

            actual.Count.Should().Be(declaredSites,
                $"{file} is registered as having exactly {declaredSites} sample-INCLUSIVE " +
                $"aggregate(s) over PurchaseOrders ({why}), but the scan found {actual.Count}.\n" +
                (actual.Count > declaredSites
                    ? "A NEW aggregate was added to a file that is already on the exemption list — " +
                      "the registry must not silently cover it. Declare it or filter it.\n"
                    : "One of the registered sites now filters IsSample (or was removed). That is " +
                      "progress: lower the number here so the registry keeps describing reality.\n") +
                $"Found:\n{Render(actual)}\n");
        }
    }

    // ── scanning ──────────────────────────────────────────────────────────────

    private sealed record Site(string File, int Line, string Statement);

    private static readonly Regex DbSetMention = new(@"\bPurchaseOrders\b", RegexOptions.Compiled);

    // The terminals that turn a query into a POPULATION question. A single-row read
    // (FirstOrDefaultAsync by id) is not one — you must be able to open the practice
    // order, so those are correctly unfiltered and are not the subject of this guard.
    private static readonly Regex AggregateTerminal =
        new(@"\.(CountAsync|LongCountAsync|GroupBy)\s*\(", RegexOptions.Compiled);

    private static readonly Regex SampleAware = new(@"\bIsSample\b", RegexOptions.Compiled);

    private static (List<Site> SampleAware, List<Site> SampleInclusive) ScanProductionSources()
    {
        var root        = RepoRoot();
        var aware       = new List<Site>();
        var inclusive   = new List<Site>();

        foreach (var file in ProductionSourceFiles())
        {
            var text = File.ReadAllText(file);
            var rel  = Path.GetRelativePath(root, file).Replace('\\', '/');

            foreach (var stmt in StatementsMentioningDbSet(text))
            {
                if (!AggregateTerminal.IsMatch(stmt.Text)) continue;

                var site = new Site(rel, stmt.Line, Collapse(stmt.Text));
                (SampleAware.IsMatch(stmt.Text) ? aware : inclusive).Add(site);
            }
        }

        return (aware, inclusive);
    }

    /// <summary>
    /// Splits out each C# statement that mentions the DbSet. A statement runs from the previous
    /// statement/block boundary to its terminating semicolon, which is what makes a multi-line
    /// LINQ chain one unit — the predicate and its terminal operator are usually many lines apart,
    /// so a line-by-line scan would see neither in the company of the other.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> StatementsMentioningDbSet(string text)
    {
        var seen = new HashSet<int>();

        foreach (Match m in DbSetMention.Matches(text))
        {
            var start = m.Index;
            while (start > 0 && text[start - 1] is not (';' or '{' or '}')) start--;

            if (!seen.Add(start)) continue; // several mentions inside one statement

            var end = text.IndexOf(';', m.Index);
            if (end < 0) end = text.Length - 1;

            var line = text.Take(start).Count(c => c == '\n') + 1;
            yield return (line, text[start..(end + 1)]);
        }
    }

    private static string Collapse(string statement) =>
        Regex.Replace(statement, @"\s+", " ").Trim() is var s && s.Length > 160
            ? s[..160] + " …"
            : s;

    private static string Render(IEnumerable<Site> sites) =>
        string.Join("\n", sites.Select(s => $"  {s.File}:{s.Line}\n      {s.Statement}"));

    /// <summary>
    /// Checked-in production C#: no test projects (they seed practice orders on purpose), no
    /// build output, no worktrees, and no migration bodies (an append-only record of a past model,
    /// which cannot be edited to satisfy a convention introduced later).
    /// </summary>
    private static IEnumerable<string> ProductionSourceFiles()
    {
        var root = RepoRoot();
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) return false;
                if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) return false;
                if (rel.StartsWith(".claude/")) return false;
                if (rel.Contains("/Migrations/")) return false;
                if (rel.Contains(".Tests/")) return false;
                return true;
            });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull(
            "the tests must run from inside the ProcuLink checkout (ProcuLink.slnx not found above the test binaries)");
        return dir!.FullName;
    }
}
