using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The proof obligations for <see cref="RepoSourceCorpus"/> — the one place the architecture
/// guards are allowed to decide which files count as "this repository's source".
///
/// <para><b>The failure this closes.</b> Three guards used to call
/// <c>Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)</c> and then subtract
/// a hand-written skip list of <c>obj</c>, <c>bin</c> and <c>.claude</c>. Anything else sitting
/// beside the projects was read as production code. A parallel session left a full, untracked copy
/// of the repository at <c>&lt;root&gt;/.git-audit-e/</c> and the suite went red on a clean
/// checkout of <c>main</c>: <c>RevisionAuthorityHostCoverageTests</c> reported
/// <c>{".git-audit-e", "ProcuLink.Api", "ProcuLink.Worker"}</c> as the set of deployable hosts,
/// <c>SampleExclusionIsDeclaredNotAssumedTests</c> flagged sites in
/// <c>.git-audit-e/ProcuLink.Infrastructure/Services/OpsHealthService.cs</c>, and — because that
/// copy predated Wave 1 — all three <c>RetiredSubsystemsStayRetiredTests</c> found the retired
/// entities alive. Nobody wrote a line of the code that failed. Deleting the directory would have
/// cleared the red and left the next copy just as fatal.</para>
///
/// <para><b>Two independent rules, because either alone has a hole.</b> A directory whose name
/// starts with <c>.</c> is never descended into — that covers <c>.git-audit-e</c>,
/// <c>.claude/worktrees/</c>, <c>.vs</c> and anything else a tool drops beside the source, and it
/// holds even when git is unavailable. And when git IS available the corpus is what git tracks,
/// plus new files that are not ignored and sit under an already-tracked top-level directory — so a
/// copy named without a leading dot (<c>ProcuLink-backup/</c>, <c>audit-2026-08/</c>) is refused
/// too. A guard that reads an untracked copy is asserting about code that will never ship.</para>
/// </summary>
public class RepoSourceCorpusTests
{
    // ── The rule, stated directly ────────────────────────────────────────────────────

    [Theory]
    // Real production source — the corpus would be worthless without these.
    [InlineData("ProcuLink.Api/Program.cs", false)]
    [InlineData("ProcuLink.Infrastructure/Services/OpsHealthService.cs", false)]
    [InlineData("ProcuLink.Api/Controllers/Deep/Nested/Thing.cs", false)]
    // The directory that caused this file to exist.
    [InlineData(".git-audit-e/ProcuLink.Infrastructure/Services/OpsHealthService.cs", true)]
    [InlineData(".git-audit-e/ProcuLink.Api/Program.cs", true)]
    // Every other dot-directory, at the root and nested.
    [InlineData(".claude/worktrees/other-session/ProcuLink.Api/Program.cs", true)]
    [InlineData("ProcuLink.Api/.claude/worktrees/x/Program.cs", true)]
    [InlineData(".vs/ProjectEvaluation/Thing.cs", true)]
    [InlineData(".github/scripts/Thing.cs", true)]
    // Build output, at the root and inside a project.
    [InlineData("ProcuLink.Api/bin/Debug/net8.0/Generated.cs", true)]
    [InlineData("ProcuLink.Api/obj/Debug/net8.0/Generated.cs", true)]
    [InlineData("bin/Debug/Generated.cs", true)]
    [InlineData("obj/Debug/Generated.cs", true)]
    [InlineData("node_modules/pkg/Thing.cs", true)]
    public void TheRule_RefusesEveryDirectoryThatCannotShip(string relative, bool refused) =>
        RepoSourceCorpus.IsOutsideTheShippingTree(relative).Should().Be(refused);

    /// <summary>Windows hands out backslashes; every rule above is written with forward slashes.</summary>
    [Fact]
    public void TheRule_ReadsBackslashPathsTheSameWayAsForwardSlashPaths()
    {
        RepoSourceCorpus.IsOutsideTheShippingTree(@".git-audit-e\ProcuLink.Api\Program.cs").Should().BeTrue();
        RepoSourceCorpus.IsOutsideTheShippingTree(@"ProcuLink.Api\bin\Debug\Generated.cs").Should().BeTrue();
        RepoSourceCorpus.IsOutsideTheShippingTree(@"ProcuLink.Api\Program.cs").Should().BeFalse();
    }

    // ── Without git: the walk must not descend into a dot-directory ──────────────────

    /// <summary>
    /// A synthetic tree that is NOT a git repository, so this exercises the fallback walk — the
    /// path that has to hold when the suite runs from an export, a container image with no git, or
    /// a temp directory like this one. The sibling copy is shaped exactly like the one that broke
    /// the build: a full project tree under a dot-directory at the root.
    /// </summary>
    [Fact]
    public void TheWalk_DoesNotDescendIntoADotDirectory_EvenWhenItHoldsAWholeProjectTree()
    {
        var root = NewNonGitSandbox();

        try
        {
            Write(root, "ProcuLink.Api/Program.cs");
            Write(root, "ProcuLink.Infrastructure/Services/OpsHealthService.cs");
            Write(root, "ProcuLink.Api/bin/Debug/net8.0/Generated.cs");
            Write(root, "ProcuLink.Api/obj/Debug/net8.0/Generated.cs");
            Write(root, ".git-audit-e/ProcuLink.Api/Program.cs");
            Write(root, ".git-audit-e/ProcuLink.Infrastructure/Services/OpsHealthService.cs");
            Write(root, ".claude/worktrees/other-session/ProcuLink.Api/Program.cs");
            Write(root, ".vs/ProjectEvaluation/Thing.cs");

            Relatives(root).Should().Equal(
                new[] { "ProcuLink.Api/Program.cs", "ProcuLink.Infrastructure/Services/OpsHealthService.cs" },
                "an untracked copy of the repository is not this repository — reading it makes a "
                + "guard assert about code that will never ship, and it went red on a clean main "
                + "because of exactly this tree shape");
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The corpus must FAIL rather than report an empty set. Empty is the shape every one of these
    /// guards passes vacuously on, and the exclusions here are one bad edit away from producing it.
    /// </summary>
    [Fact]
    public void TheCorpus_ThrowsRatherThanReportingNothing()
    {
        var root = NewNonGitSandbox();

        try
        {
            Write(root, ".git-audit-e/ProcuLink.Api/Program.cs");

            var act = () => RepoSourceCorpus.CsFiles(root);

            act.Should().Throw<InvalidOperationException>(
                "a scan that finds no source must be loud — a guard handed an empty corpus reports "
                + "green having examined nothing")
                .WithMessage("*no C# source*");
        }
        finally
        {
            Delete(root);
        }
    }

    // ── With git: tracked-only, so a copy without a leading dot is refused too ───────

    /// <summary>
    /// The half the dot-rule cannot do. A repository copy named <c>audit-copy/</c> passes every
    /// name-shaped filter ever written; it is refused here because git does not track it and its
    /// top-level directory is not one git tracks either. The same test pins the cost of that rule:
    /// a brand-new file that has not been staged yet, sitting inside a project git DOES track, must
    /// still be scanned — otherwise every guard would go green on code the author just wrote and
    /// red the moment CI saw it.
    /// </summary>
    [Fact]
    public void TheGitCorpus_RefusesAnUntrackedCopy_YetStillSeesAnUnstagedFileInsideARealProject()
    {
        RequireGit();

        var root = NewSandbox();

        try
        {
            Write(root, "ProcuLink.Api/Program.cs");
            Write(root, "ProcuLink.Infrastructure/Services/OpsHealthService.cs");
            Git(root, "init -q");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name Test");
            Git(root, "add -A");
            Git(root, "commit -q -m seed");

            // Untracked and NOT dot-prefixed: only git can tell this apart from real source.
            Write(root, "audit-copy/ProcuLink.Api/Program.cs");
            Write(root, "audit-copy/ProcuLink.Infrastructure/Services/OpsHealthService.cs");

            // Untracked, but inside a directory git tracks — a file the author just wrote.
            Write(root, "ProcuLink.Api/Services/JustWritten.cs");

            Relatives(root).Should().Equal(
                new[]
                {
                    "ProcuLink.Api/Program.cs",
                    "ProcuLink.Api/Services/JustWritten.cs",
                    "ProcuLink.Infrastructure/Services/OpsHealthService.cs",
                },
                "git is what separates a copy of the repository from the repository: audit-copy/ is "
                + "untracked and its top-level directory is untracked, so it cannot ship; "
                + "JustWritten.cs is untracked but sits under a tracked project, so it will");
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A tracked file that has been deleted on disk but not yet staged is still in git's index.
    /// Handing its path to <c>File.ReadAllText</c> throws, which would take every guard down with
    /// an IO error instead of an architecture verdict.
    /// </summary>
    [Fact]
    public void TheGitCorpus_DropsATrackedFileThatIsNoLongerOnDisk()
    {
        RequireGit();

        var root = NewSandbox();

        try
        {
            Write(root, "ProcuLink.Api/Program.cs");
            Write(root, "ProcuLink.Api/Services/Deleted.cs");
            Git(root, "init -q");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name Test");
            Git(root, "add -A");
            Git(root, "commit -q -m seed");

            File.Delete(Path.Combine(root, "ProcuLink.Api", "Services", "Deleted.cs"));

            Relatives(root).Should().Equal(
                new[] { "ProcuLink.Api/Program.cs" },
                "git still lists the deleted file in its index, and a corpus that hands out paths "
                + "which do not exist turns every guard into an IO error");
        }
        finally
        {
            Delete(root);
        }
    }

    // ── Against the real repository ──────────────────────────────────────────────────

    /// <summary>
    /// The corpus, read from the checkout the suite is actually running in — a worktree under
    /// <c>.claude/worktrees/</c> for most sessions in this project, which is the case that used to
    /// exclude the entire repository.
    /// </summary>
    [Fact]
    public void TheCorpus_SeesThisCheckoutsOwnProductionSources_AndNothingItCannotShip()
    {
        var root = RepoSourceCorpus.FindRepoRoot();

        var relatives = RepoSourceCorpus.CsFiles(root).Select(f => f.Relative).ToList();

        relatives.Should().Contain("ProcuLink.Api/Program.cs");
        relatives.Should().Contain("ProcuLink.Worker/Program.cs");
        relatives.Should().Contain("ProcuLink.Infrastructure/Services/OpsHealthService.cs");
        relatives.Should().Contain("ProcuLink.Api.Tests/Architecture/RepoSourceCorpus.cs",
            "the corpus is the whole repository's C#; narrowing to production is each guard's own job");

        relatives.Should().OnlyContain(r => !RepoSourceCorpus.IsOutsideTheShippingTree(r));
        relatives.Should().OnlyContain(r => File.Exists(Path.Combine(root, r.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    /// The two corpora, related in the one direction that is always true: everything git reports is
    /// on disk, so the walk must find all of it. The reverse does NOT hold, and that asymmetry is
    /// the point — the walk also finds untracked copies sitting beside the source, which is the
    /// entire defect. Stated as a test so a future edit cannot quietly make the git path narrower
    /// than the fallback.
    /// </summary>
    [Fact]
    public void TheGitCorpus_IsContainedInTheFallbackWalk_ForThisRepository()
    {
        RequireGit();

        var root = RepoSourceCorpus.FindRepoRoot();

        var walked = RepoSourceCorpus.WalkedCsFiles(root).ToHashSet(StringComparer.Ordinal);
        var tracked = RepoSourceCorpus.GitCsFiles(root);

        tracked.Should().NotBeNull("this repository is a git checkout, so the git corpus must resolve");
        tracked!.Should().NotBeEmpty();
        walked.Should().NotBeEmpty();

        tracked.Except(walked, StringComparer.Ordinal).Should().BeEmpty(
            "every file git reports exists on disk under a directory the walk descends into — a "
            + "difference here means the two corpora disagree about what this repository is");
    }

    // ── Regression: no guard may go back to walking the repository root ──────────────

    /// <summary>
    /// The durable half. Fixing three call sites fixes three call sites; this fails the moment a
    /// fourth guard is written with <c>Directory.EnumerateFiles(root, …)</c> against the repository
    /// root, which is how this defect arrived in the first place. Enumerating a NAMED project
    /// directory (<c>Path.Combine(root, "ProcuLink.Api")</c>) stays allowed — a sibling copy cannot
    /// reach it.
    /// </summary>
    [Fact]
    public void NoOtherGuard_EnumeratesTheRepositoryRootDirectly()
    {
        var root = RepoSourceCorpus.FindRepoRoot();

        var offenders = RepoSourceCorpus.CsFiles(root)
            .Where(f => f.Relative.StartsWith("ProcuLink.Api.Tests/Architecture/", StringComparison.Ordinal)
                        || f.Relative.StartsWith("ProcuLink.Api.Tests/Meta/", StringComparison.Ordinal))
            .Where(f => f.Relative != "ProcuLink.Api.Tests/Architecture/RepoSourceCorpus.cs"
                        && f.Relative != "ProcuLink.Api.Tests/Architecture/RepoSourceCorpusTests.cs")
            .Where(f => RootWalk.IsMatch(File.ReadAllText(f.FullPath)))
            .Select(f => f.Relative)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "an architecture guard that walks the repository root reads whatever a parallel session "
            + "left beside the projects — an untracked copy under .git-audit-e/ made four tests fail "
            + "on a clean main, and .claude/worktrees/ does it identically for every session that "
            + "follows CLAUDE.md and works in a worktree. Go through RepoSourceCorpus.CsFiles, or "
            + "enumerate a NAMED project directory. Offender(s): " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The test above is a text scan, and a text scan that matches nothing looks identical to one
    /// whose subject is clean. This proves the pattern still recognises the shape it bans.
    /// </summary>
    [Fact]
    public void TheRootWalkPattern_StillMatchesTheShapeItBans()
    {
        RootWalk.IsMatch(@"Directory.EnumerateFiles(root, ""*.cs"", SearchOption.AllDirectories)").Should().BeTrue();
        RootWalk.IsMatch("Directory\n            .EnumerateFiles(root, \"*.cs\", SearchOption.AllDirectories)").Should().BeTrue(
            "the three guards that failed all wrote it across two lines");
        RootWalk.IsMatch(@"Directory.EnumerateDirectories(repoRoot)").Should().BeTrue();
        RootWalk.IsMatch(@"Directory.EnumerateDirectories(repoRoot, glob, SearchOption.TopDirectoryOnly)").Should().BeTrue();
        RootWalk.IsMatch(@"Directory.EnumerateFiles(RepoRoot(), ""*.cs"")").Should().BeTrue();
        RootWalk.IsMatch(@"Directory.EnumerateFiles(FindRepoRoot(), ""*.cs"")").Should().BeTrue();

        RootWalk.IsMatch(@"Directory.EnumerateFiles(projectDir, ""*.cs"", SearchOption.AllDirectories)").Should().BeFalse(
            "enumerating a named project directory is the safe shape and must not be flagged");
        RootWalk.IsMatch(@"Directory.EnumerateFiles(Path.Combine(root, project), ""*.cs"")").Should().BeFalse(
            "Path.Combine(root, project) cannot reach a sibling copy");
        RootWalk.IsMatch(@"Directory.EnumerateFiles(migrationsDir, ""*.cs"", SearchOption.AllDirectories)").Should().BeFalse();
    }

    /// <summary>
    /// The banned shape: a recursive enumeration whose root IS the repository root. Named project
    /// directories are deliberately not matched — a sibling copy cannot reach
    /// <c>Path.Combine(root, "ProcuLink.Api")</c>.
    ///
    /// <para><b>Known limit, stated so nobody mistakes this for a proof.</b> It recognises the
    /// repository root by the four spellings every guard in this repository uses today
    /// (<c>root</c>, <c>repoRoot</c>, <c>RepoRoot()</c>, <c>FindRepoRoot()</c>). A new guard that
    /// binds the root to some other local — <c>checkoutRoot</c>, <c>solutionDir</c> — walks past
    /// this. It is a tripwire on the idiom, not a type system: the real defence is
    /// <see cref="RepoSourceCorpus"/> being the obvious thing to reach for. Widen the alternation
    /// when a fifth spelling appears rather than assuming silence means clean.</para>
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RootWalk = new(
        @"Directory\s*\.\s*Enumerate(Files|Directories)\s*\(\s*(root|repoRoot|RepoRoot\(\)|FindRepoRoot\(\))\s*[,)]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static List<string> Relatives(string root) =>
        RepoSourceCorpus.CsFiles(root)
            .Select(f => f.Relative)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    private static string NewSandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plk-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// A sandbox that must exercise the FALLBACK WALK, with its premise checked rather than
    /// assumed: the walk only runs when git cannot answer, and git can answer for any directory
    /// that happens to sit inside a repository. If someone's <c>TMPDIR</c> ever does, these tests
    /// would otherwise fail with a confusing "found no C# source" — every file in the sandbox is
    /// untracked, so the git corpus comes back empty rather than null — instead of saying that the
    /// premise, not the code, changed.
    /// </summary>
    private static string NewNonGitSandbox()
    {
        var root = NewSandbox();

        RepoSourceCorpus.GitCsFiles(root).Should().BeNull(
            $"this test exercises the no-git fallback, which requires '{root}' to sit outside any "
            + "git repository. It does not, so the fallback walk is not what ran here.");

        return root;
    }

    private static void Delete(string root)
    {
        if (!Directory.Exists(root)) return;

        // git marks objects read-only on Windows, and Directory.Delete refuses those.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private static void Write(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "// synthetic source — only the path matters here\n");
    }

    private static void RequireGit() =>
        RepoSourceCorpus.GitIsAvailable().Should().BeTrue(
            "these rules are about what git tracks, and a run without git cannot check them. This "
            + "is deliberately a failure rather than a skip: a skipped guard reports green.");

    private static void Git(string root, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);

        process.ExitCode.Should().Be(0, $"`git {arguments}` must succeed to set the fixture up: {stderr}");
    }
}
