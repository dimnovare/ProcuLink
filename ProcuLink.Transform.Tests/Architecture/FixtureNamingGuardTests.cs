using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The sibling of <see cref="FixtureDeIdentificationGuardTests"/>, pointed the other way.
///
/// That guard scrubs fixture CONTENTS. It was written from a content instance — a local filesystem
/// path and some live e-mail addresses inside two files — and so it is blind to the same defect
/// rotated ninety degrees: seven committed fixtures were named after the real companies whose
/// purchase orders they were built from. <b>This repository is public, and on a public repository a
/// file name is exactly as readable as the file's contents</b> — more so, because a name shows up in
/// a directory listing, in a CI log, in a stack trace and in code search without anybody opening
/// anything.
///
/// <para><b>Why this is an allow-list and not a deny-list.</b> The obvious guard is a list of real
/// company names to refuse. That guard cannot be written here: committing a list of real company
/// names, to a public repository, in order to prove that no real company names are committed,
/// republishes precisely what it is meant to suppress — and it would survive any later scrub of the
/// history, because it would be sitting in the current tree. It is also the weaker guard on the
/// merits: a deny-list only ever catches the companies whoever wrote it happened to think of.</para>
///
/// <para>So the rule is inverted. Fixture names are drawn from a closed vocabulary that describes
/// the FORMAT, the DIALECT and the SHAPE a fixture pins — <c>cxml</c>, <c>idoc</c>, <c>punchout</c>,
/// <c>2lines</c>, <c>no_dtd</c> — and any token outside it fails. No real company name is stored
/// anywhere, and a counterparty nobody has heard of is caught just as reliably as a famous one.
/// Adding a word is a deliberate, reviewable edit, which is the moment somebody gets to ask "is
/// that a format, or is that a customer?"</para>
///
/// <para>The vocabulary does name a few companies — <c>ariba</c>, <c>coupa</c>, <c>directo</c>,
/// <c>erply</c>. Those are procurement platforms and ERPs whose document dialects ProcuLink
/// publicly supports, and naming the dialect is the entire documentary value of the file name. The
/// distinction the guard enforces is <b>platform (a format we speak) versus counterparty (a party
/// we traded with)</b>. Nothing confidential is disclosed by saying which cXML flavour a fixture is;
/// a great deal is disclosed by saying whose purchase order it was.</para>
///
/// <para>Failures name the file and the class of problem, never the offending value — the token is
/// replaced with <c>[redacted]</c> in the reported path. CI logs on a public repository are public
/// too, and a guard that prints the leak in order to prove the leak has not removed it. Open the
/// named path locally to see what tripped.</para>
/// </summary>
public class FixtureNamingGuardTests
{
    // ── Vocabulary ───────────────────────────────────────────────────────────────────────
    //
    // Every token a fixture path may use. Purely numeric tokens (versions, counts, segment
    // numbers) are allowed without being listed. Digits are stripped before lookup, so
    // "2lines" is checked as "lines" and "orders05" as "orders".
    //
    // NOTHING IN THIS LIST MAY BE A TRADING COUNTERPARTY. Platforms and standards only.
    private static readonly HashSet<string> ApprovedTokens = new(StringComparer.Ordinal)
    {
        // Repository / project / directory structure.
        "proculink", "api", "transform", "tests", "fixtures", "catalog", "formatmatrix",
        "realworldfixtures", "po", "templates",

        // Document formats and standards.
        "cxml", "idoc", "orders", "csv", "json", "xml", "excel", "cif", "soap",
        "stoitembase", "ubl", "edifact",

        // Platform / ERP dialects that ProcuLink publicly supports. A dialect is a format,
        // not a customer — see the class doc before adding to this group.
        "ariba", "coupa", "directo", "erply", "punchout",

        // Roles and canonical concepts.
        "buyer", "supplier", "order", "orderrequest", "part", "mpn", "index", "output",
        "sample", "expected", "generic", "real", "rw",

        // Shapes, quirks and behaviours a fixture exists to pin.
        "bare", "curcy", "deploymentmode", "differs", "dtd", "equals", "lines", "netwr",
        "no", "numeric", "reconcile", "singleline", "unicode", "with",

        // Locales and currencies.
        "es", "fr", "it", "pl", "de", "en", "eur", "pln", "sek",
    };

    /// <summary>Splits a path segment while keeping the delimiters, so a redacted path can be
    /// rebuilt character-for-character apart from the token that was removed.</summary>
    private static readonly Regex SegmentSplitter = new(@"([-_. ])", RegexOptions.Compiled);

    // ── Detector ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises one raw token for vocabulary lookup: lower-cased, ASCII digits removed.
    /// Returns <c>null</c> for a token that is purely numeric (or empty), which is always allowed.
    /// Digit-stripping does not open a hole — a two-character company name like "7Q" normalises to
    /// "q", and "inventedfoods2" to "inventedfoods", and neither is in the vocabulary. (The
    /// examples here are invented on purpose: a doc comment that illustrated the rule with the
    /// real company names this guard exists to remove would be committing them in order to
    /// explain why they must not be committed.)
    /// </summary>
    internal static string? Normalise(string token)
    {
        var normalised = new string(token.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return normalised.Length == 0 ? null : normalised;
    }

    /// <summary>
    /// Every violation in one repo-relative fixture path, as a message that names the path with
    /// the offending token replaced by <c>[redacted]</c>. Empty when the path is clean.
    /// </summary>
    internal static IReadOnlyList<string> PathViolations(string relativePath)
    {
        var found = new List<string>();
        var segments = relativePath.Split('/');

        for (var s = 0; s < segments.Length; s++)
        {
            var parts = SegmentSplitter.Split(segments[s]);

            for (var p = 0; p < parts.Length; p++)
            {
                // Odd indices are the captured delimiters, not tokens.
                if (p % 2 != 0)
                {
                    continue;
                }

                var normalised = Normalise(parts[p]);
                if (normalised is null || ApprovedTokens.Contains(normalised))
                {
                    continue;
                }

                var kind = s == segments.Length - 1 ? "file name" : "directory name";
                found.Add(
                    $"  {Redact(segments, s, parts, p)}: {kind} token is not in the approved "
                    + "fixture-naming vocabulary");
            }
        }

        return found;
    }

    /// <summary>Rebuilds the path with exactly one token replaced by <c>[redacted]</c>.</summary>
    private static string Redact(string[] segments, int segmentIndex, string[] parts, int partIndex)
    {
        var redactedParts = (string[])parts.Clone();
        redactedParts[partIndex] = "[redacted]";

        var redactedSegments = (string[])segments.Clone();
        redactedSegments[segmentIndex] = string.Concat(redactedParts);

        return string.Join('/', redactedSegments);
    }

    // ── The guard ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackedFixturePaths_NameNoRealWorldParty()
    {
        var scan = ScanFixturePaths();

        scan.Violations.Should().BeEmpty(
            "a fixture's name must describe the format, the dialect and the shape it pins — never "
            + "the party whose document it was built from. This repository is public, so a file "
            + "name is as readable as its contents. Rename the file, or add the token to "
            + "ApprovedTokens if it genuinely describes a format rather than a counterparty. "
            + "Offending path and class (the token itself is withheld: this repository and its CI "
            + "logs are public):\n"
            + string.Join("\n", scan.Violations));
    }

    /// <summary>
    /// Anti-vacuity. A file-count floor alone is not a detection floor: the corpus could be
    /// enumerated correctly and every path still go unexamined. The token floor is the one that
    /// proves the paths were actually split and looked up, and the directory floor proves the
    /// sweep reaches more than one fixture folder.
    /// </summary>
    [Fact]
    public void FixturePathSweep_ActuallyExaminesTheCorpus()
    {
        var scan = ScanFixturePaths();

        scan.ScannedFiles.Should().BeGreaterThanOrEqualTo(
            20,
            $"only {scan.ScannedFiles} tracked fixture files were found under {scan.RepoRoot} "
            + "— the corpus query is broken, so the guard above proves nothing");

        scan.DistinctDirectories.Should().BeGreaterThanOrEqualTo(
            4,
            $"only {scan.DistinctDirectories} distinct fixture directories were swept — the corpus "
            + "query has narrowed to one folder, so the guard above proves nothing about the rest");

        scan.ScannedTokens.Should().BeGreaterThanOrEqualTo(
            100,
            $"only {scan.ScannedTokens} path tokens were examined — the paths were listed but not "
            + "actually tokenised, so the guard above proves nothing");
    }

    /// <summary>
    /// Positive control. The sweep going green means something only if the detector can still go
    /// red. Every company named below is fictitious and belongs to nobody — which is the point:
    /// the detector needs no knowledge of any real company to refuse these.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/rw_cxml_fictitiousvendor_pl.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/rw-idoc-imaginarycorp-fr.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/real-madeupgmbh-cxml-1.2.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/InventedHoldings/rw_cxml_es.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/cxml order from nowhereco.xml")]
    public void Detector_RefusesAPathThatNamesAParty(string path)
        => PathViolations(path).Should().NotBeEmpty();

    /// <summary>
    /// The redaction is itself load-bearing: a failure message that echoed the token would leak it
    /// into a public CI log, which is the thing this guard exists to prevent.
    /// </summary>
    [Fact]
    public void Detector_WithholdsTheOffendingTokenFromTheFailureMessage()
    {
        const string token = "fictitiousvendor";
        var violations = PathViolations($"ProcuLink.Transform.Tests/Fixtures/rw_cxml_{token}_pl.xml");

        var message = violations.Should().ContainSingle().Subject;
        message.Should().NotContain(token, "the failure message must never echo the offending value");
        message.Should().Contain("[redacted]");
        message.Should().Contain("rw_cxml_", "the rest of the path must survive so the file is findable");
        message.Should().Contain("_pl.xml");
    }

    /// <summary>
    /// Negative control. A detector that refuses everything is as useless as one that refuses
    /// nothing — it gets suppressed within a week. These are the naming patterns the convention
    /// is supposed to permit.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Transform.Tests/FormatMatrix/RealWorldFixtures/rw_cxml_pl_2lines_pln_no_dtd.xml")]
    [InlineData("ProcuLink.Transform.Tests/FormatMatrix/RealWorldFixtures/rw_idoc_es_numeric_curcy704.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/real-cxml-1.2-ariba-punchout-mpn-differs.xml")]
    [InlineData("ProcuLink.Transform.Tests/Fixtures/Idoc/idoc-orders05-710.xml")]
    [InlineData("ProcuLink.Transform.Tests/Catalog/Fixtures/cif-3.0.cif")]
    [InlineData("ProcuLink.Api/Fixtures/po-templates/generic-csv.json")]
    public void Detector_PermitsAFormatShapedName(string path)
        => PathViolations(path).Should().BeEmpty();

    // ── Corpus ───────────────────────────────────────────────────────────────────────────

    private sealed record PathScan(
        string RepoRoot,
        int ScannedFiles,
        int ScannedTokens,
        int DistinctDirectories,
        IReadOnlyList<string> Violations);

    private static PathScan ScanFixturePaths()
    {
        var repoRoot = FixtureCorpus.FindRepoRoot();
        var violations = new List<string>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = 0;
        var scannedTokens = 0;

        foreach (var relative in FixtureCorpus.TrackedFixtureFiles(repoRoot))
        {
            scannedFiles++;

            var lastSlash = relative.LastIndexOf('/');
            directories.Add(lastSlash < 0 ? string.Empty : relative[..lastSlash]);

            foreach (var segment in relative.Split('/'))
            {
                scannedTokens += SegmentSplitter
                    .Split(segment)
                    .Where((_, i) => i % 2 == 0)
                    .Count(part => Normalise(part) is not null);
            }

            violations.AddRange(PathViolations(relative));
        }

        return new PathScan(repoRoot, scannedFiles, scannedTokens, directories.Count, violations);
    }
}
