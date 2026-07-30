using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The tripwire for the defect class "two comparers over one column".
///
/// <para><b>The history this exists to stop repeating.</b> A buyer item code and a supplier product
/// code were compared in one place with an ordinal <c>==</c> and in another with
/// <c>OrdinalIgnoreCase</c>. Each site looked correct on its own, so each survived review; together
/// they meant the same code resolved through the catalog and not through the learned mapping, and a
/// supplier's lower-cased export scored zero on auto-detect while resolving fine once routed.
/// <c>ItemCodeComparison</c> was introduced as the ONE rule — and then the very next change added a
/// third site with a third comparer. Naming a rule does not enforce it; this test does.</para>
///
/// <para><b>What it checks.</b> Every line in the shipped source (not tests) that COMPARES, DEDUPES,
/// GROUPS or KEYS A DICTIONARY on one of those two columns must either reference
/// <c>ItemCodeComparison</c> nearby, or appear in <see cref="AcceptedExceptions"/> with a written
/// reason. It is deliberately a text scan rather than a Roslyn analyzer: the comparer choice is
/// visible in the source text, and a test that runs in the normal suite catches the fourth site at
/// the moment it is written, which an analyzer nobody installs does not.</para>
///
/// <para><b>What it deliberately does NOT reach.</b> A comparer chosen on a line that names neither
/// the column nor a variable spelled after it — <c>new Dictionary&lt;string, string?&gt;(count,
/// StringComparer.Ordinal)</c>, where only the surrounding prose says the keys are item codes — is
/// invisible to a text scan and would need dataflow analysis. Those sites carry their reasoning in
/// a source comment instead. The guard covers the shapes the defect has actually taken (a hand
/// comparer on a column member, and an explicit comparer inside a chain that starts at one), not
/// every conceivable one; a partial tripwire that is read beats a total one that is disabled.</para>
///
/// <para><b>Rot-resistance runs both ways</b>, matching <c>CanonicalRowFieldsCompletenessTests</c>:
/// a NEW unguarded site fails <see cref="EveryItemCodeComparison_UsesTheSharedRule_OrIsAnAcceptedException"/>,
/// and an exception whose site has since been deleted or fixed fails
/// <see cref="EveryAcceptedException_StillMatchesARealSite"/>, so the list cannot rot into a
/// permanent blanket. Every reason is checked for substance by
/// <see cref="EveryAcceptedException_CarriesASubstantiveWrittenReason"/>.</para>
/// </summary>
public class ItemCodeComparerGuardTests
{
    /// <summary>Projects whose shipped source is scanned. Test projects are excluded on purpose.</summary>
    private static readonly string[] ScannedProjects =
    {
        "ProcuLink.Api", "ProcuLink.Infrastructure", "ProcuLink.Core", "ProcuLink.Transform", "ProcuLink.Worker",
    };

    /// <summary>
    /// Directories inside those projects that are machine-generated or otherwise not hand-authored
    /// comparison logic.
    /// </summary>
    private static readonly string[] SkippedDirectories = { "Migrations", "obj", "bin" };

    /// <summary>
    /// A statement names one of the two governed columns when it mentions a buyer item code (the
    /// property, or a variable/parameter named after it), or a <c>.Code</c> member in a file that
    /// works with <c>SupplierProduct</c>. The second half is file-scoped so unrelated "codes" (rule
    /// codes, currency codes, country codes) do not flood the exception list and drain its meaning.
    /// </summary>
    private static readonly Regex BuyerCodeMention =
        new(@"buyeritemcode", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProductCodeMention =
        new(@"\b(?:p|product|prod|draft|d|kv|x|h|s\.Product|entry)\.Code\b", RegexOptions.Compiled);

    /// <summary>
    /// The column name inside a STRING LITERAL is a field-name match (a PDF header probe, a JSON
    /// key, an acceptance-rule path), never a comparison of two item codes.
    /// </summary>
    private static readonly Regex QuotedColumnName =
        new("\"[^\"]*buyeritemcode[^\"]*\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Constructs that DECIDE equality, flagged when the SAME LINE names a governed column. A line
    /// merely reading or projecting a code (<c>Select(p =&gt; p.Code)</c>, a DTO initialiser, a log
    /// message) is not a comparison and is not flagged.
    /// </summary>
    private static readonly Regex ComparisonShaped = new(
        @"StringComparer\.|StringComparison\.|\.Distinct\(|\.ToDictionary\(|\.ToHashSet\(|\.GroupBy\(" +
        @"|\.Contains\(|string\.Equals\(|\.TryGetValue\(|\.ContainsKey\(|\.TryAdd\(|==|!=",
        RegexOptions.Compiled);

    /// <summary>
    /// The narrower set used when the column is named EARLIER IN THE SAME STATEMENT rather than on
    /// the line itself — an explicit comparer choice inside a fluent chain
    /// (<c>buyerItemCodes.Select(…).Distinct(StringComparer.Ordinal)</c>). Bare <c>==</c> is not
    /// included: over a multi-line statement it matches unrelated members (<c>Quantity == 0m</c>)
    /// and would bury the real findings in noise, which is how a guard stops being read.
    /// </summary>
    private static readonly Regex ExplicitComparerChoice =
        new(@"StringComparer\.|StringComparison\.", RegexOptions.Compiled);

    /// <summary>The shared rule, or a form of it, appearing on or near the flagged line.</summary>
    private static readonly Regex UsesSharedRule =
        new(@"ItemCodeComparison|ProductKeyNormalizer", RegexOptions.Compiled);

    /// <summary>How many lines above/below a flagged line may carry the ItemCodeComparison reference.</summary>
    private const int ContextLines = 3;

    /// <summary>
    /// Sites that compare a governed column WITHOUT the shared rule on purpose. Each entry is
    /// (file, a distinctive fragment of the line, why). The reason is the point: an entry without a
    /// real one is indistinguishable from someone silencing the guard.
    /// </summary>
    private static readonly (string File, string Fragment, string Reason)[] AcceptedExceptions =
    {
        ("ItemMappingService.cs", "StringComparison.Ordinal))",
            "PickDeterministically's exact-case preference. This IS the shared rule's tie-break: when "
            + "two case-variant rows both match, the row spelled exactly as requested wins. It has to "
            + "be an ordinal comparison — that is what 'exact case' means — and folding it here would "
            + "make the tie-break vacuous."),

        ("ItemMappingService.cs", "m.BuyerItemCode, normalisedCode, StringComparison.Ordinal",
            "FindExistingAsync's exact-case preference, the write-side twin of the line above. Same "
            + "reason: it selects WHICH matching row to update, after the case-insensitive fetch has "
            + "already decided which rows match."),

        ("ItemMappingService.cs", "Distinct(StringComparer.Ordinal)",
            "ResolveManyAsync keys its result by the caller's exact code. Case-insensitivity belongs "
            + "in the match against stored rows, never in the result keying: folding the keys answers "
            + "a 'b-1' line from a 'B-1' row and the WRONG ITEM is ordered. The match below this line "
            + "does use the shared rule."),

        ("OrderIngestionService.cs", "Distinct(StringComparer.Ordinal)",
            "ResolveFromSnapshot mirrors ResolveManyAsync's Ordinal keying exactly, for the same "
            + "reason. Its MATCH against snapshot rows folds case via ItemCodeComparison."),

        ("OrderIngestionService.cs", "row.BuyerItemCode, code, StringComparison.Ordinal",
            "ResolveFromSnapshot's exact-case tie-break, aligned with PickDeterministically so the "
            + "replay picks the same twin the live resolver picked."),

        ("SupplierCatalogService.cs", "existing.ToDictionary(p => p.Code, StringComparer.Ordinal)",
            "Catalog IMPORT is deliberately case-sensitive while every catalog READ folds case. "
            + "Folding here would collapse two rows of one supplier feed and DELETE a product; the "
            + "item code is a namespace the supplier controls and an importer must not decide two of "
            + "their SKUs are the same thing. The read side resolves any resulting twin "
            + "deterministically (BuildCatalogLookupAsync orders by Code then Id). Full reasoning is "
            + "in the comment at the site."),

        ("ReplayService.cs", "map.TryGetValue(buyerItemCode.Trim()",
            "ResolveCodeFromMap names NO comparer on purpose: it inherits the dictionary it is "
            + "handed, and both suppliers (ItemMappingService.ResolveManyAsync and "
            + "OrderIngestionService.ResolveFromSnapshot) key Ordinal by the exact trimmed code the "
            + "line carries. Folding here would answer a 'b-1' line from a 'B-1' key and report a "
            + "code change that never happened."),

        ("OrderIngestionService.cs", "map.TryGetValue(buyerItemCode.Trim()",
            "BuildLineEntitiesAsync's local ResolveFromMap — same contract as ReplayService's copy "
            + "above, reading the same two dictionaries. Its comparer must come from whichever "
            + "resolver produced the map, never from here."),

        ("SupplierCatalogService.cs", "codes.Contains(p.Code)",
            "The existing-row fetch for the same deliberately case-sensitive import batch. It must "
            + "match the byCode dictionary that consumes it — folding only this query would fetch a "
            + "case-variant row the Ordinal dictionary then fails to find, producing an INSERT that "
            + "violates nothing but resurrects the twin anyway."),
    };

    // ── The scan ─────────────────────────────────────────────────────────────────

    private sealed record Site(string File, int Line, string Text);

    private static bool NamesColumn(string text, bool isProductFile) =>
        (BuyerCodeMention.IsMatch(text) && !QuotedColumnName.IsMatch(text))
        || (isProductFile && ProductCodeMention.IsMatch(text));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the guard scans the repo source, so it must be able to find the repo root");
        return dir!.FullName;
    }

    private static List<Site> FlaggedSites()
    {
        var root  = RepoRoot();
        var sites = new List<Site>();

        foreach (var project in ScannedProjects)
        {
            var projectDir = Path.Combine(root, project);
            if (!Directory.Exists(projectDir)) continue;

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (SkippedDirectories.Any(d =>
                        relative.Contains($"{Path.DirectorySeparatorChar}{d}{Path.DirectorySeparatorChar}")))
                    continue;

                var text  = File.ReadAllText(file);
                var lines = File.ReadAllLines(file);

                // The product-code half only applies to files that actually work with the entity.
                var isProductFile = text.Contains("SupplierProduct");

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    // Comments explain comparisons; they do not perform them.
                    if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;

                    // Tier 1 — the line itself names a governed column AND decides equality.
                    var flagged = NamesColumn(line, isProductFile) && ComparisonShaped.IsMatch(line);

                    // Tier 2 — the column is named earlier in the SAME statement and this line picks
                    // a comparer explicitly. A fluent chain
                    // (`var requested = buyerItemCodes` / `.Select(…)` / `.Distinct(comparer)`) puts
                    // the comparer several lines below the only mention of the column.
                    if (!flagged && ExplicitComparerChoice.IsMatch(line))
                        flagged = NamesColumn(StatementText(lines, i), isProductFile);

                    if (!flagged) continue;

                    // The shared rule may sit a line or two away (a `key` local, a comparer variable).
                    var from = Math.Max(0, i - ContextLines);
                    var to   = Math.Min(lines.Length - 1, i + ContextLines);
                    var near = string.Join('\n', lines[from..(to + 1)]);
                    if (UsesSharedRule.IsMatch(near)) continue;

                    // An EF predicate that folds case in SQL is the shared rule expressed for the
                    // database: `m.BuyerItemCode.ToLower() == key` is exactly what ItemCodeComparison.Key
                    // is built to be compared against.
                    if (near.Contains(".ToLower()")) continue;

                    sites.Add(new Site(Path.GetFileName(file), i + 1, line.Trim()));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// The C# statement <paramref name="index"/> belongs to, reconstructed by walking back over
    /// continuation lines. A statement is taken to start after the nearest preceding line that ends
    /// a statement or a block (<c>;</c> <c>{</c> <c>}</c>) or is blank. Bounded so a pathological
    /// file cannot make this quadratic.
    /// </summary>
    private static string StatementText(string[] lines, int index)
    {
        const int MaxLookBack = 8;
        var start = index;

        for (var back = 0; back < MaxLookBack && start > 0; back++)
        {
            var previous = lines[start - 1].TrimEnd();
            var stripped = previous.TrimStart();

            if (previous.Length == 0) break;
            if (stripped.StartsWith("//")) break;
            if (previous.EndsWith(';') || previous.EndsWith('{') || previous.EndsWith('}')) break;

            start--;
        }

        return string.Join('\n', lines[start..(index + 1)]);
    }

    private static bool IsAccepted(Site site) =>
        AcceptedExceptions.Any(e =>
            string.Equals(e.File, site.File, StringComparison.OrdinalIgnoreCase)
            && site.Text.Contains(e.Fragment, StringComparison.Ordinal));

    // ── The assertions ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryItemCodeComparison_UsesTheSharedRule_OrIsAnAcceptedException()
    {
        var unguarded = FlaggedSites().Where(s => !IsAccepted(s)).ToList();

        unguarded.Should().BeEmpty(
            "every comparison of a buyer item code or a supplier product code must go through "
            + "ItemCodeComparison, or say in ItemCodeComparerGuardTests.AcceptedExceptions why it does "
            + "not. Unguarded sites found:\n"
            + string.Join('\n', unguarded.Select(s => $"  {s.File}:{s.Line}  {s.Text}")));
    }

    [Fact]
    public void EveryAcceptedException_StillMatchesARealSite()
    {
        // Stops the list rotting into a permanent blanket: an exception for a line that has since
        // been fixed or deleted must be removed, or the next unguarded line in that file inherits
        // its cover.
        var sites = FlaggedSites();

        foreach (var (file, fragment, _) in AcceptedExceptions)
        {
            sites.Should().Contain(
                s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)
                     && s.Text.Contains(fragment, StringComparison.Ordinal),
                "the accepted exception for '{0}' in {1} no longer matches any flagged line — delete "
                + "the entry", fragment, file);
        }
    }

    [Fact]
    public void EveryAcceptedException_CarriesASubstantiveWrittenReason()
    {
        // A reason of "ok" or "" would technically satisfy the other two tests while defeating the
        // point of the list.
        foreach (var (file, fragment, reason) in AcceptedExceptions)
        {
            reason.Trim().Should().NotBeNullOrWhiteSpace(
                "the exception for '{0}' in {1} needs a reason", fragment, file);
            reason.Trim().Length.Should().BeGreaterThan(60,
                "the reason for '{0}' in {1} must explain WHY ordinal is correct there, not just "
                + "assert it: '{2}'", fragment, file, reason);
        }
    }

    [Fact]
    public void TheScanner_ActuallyFindsSomething()
    {
        // Non-vacuity: a regex that matched nothing would make all three tests above pass forever.
        FlaggedSites().Should().NotBeEmpty(
            "the guard's detector must still match the known ordinal sites — if this is empty the "
            + "patterns have drifted away from the code and the guard is inert");
    }
}
