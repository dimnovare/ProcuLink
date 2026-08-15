using System.Diagnostics;
using System.Text;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The one place an architecture guard decides which files are "this repository's source".
///
/// <para><b>Why this exists.</b> The guards used to each walk the repository root with
/// <c>Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)</c> and subtract a
/// hand-written list of <c>obj</c>, <c>bin</c> and <c>.claude</c>. Everything else beside the
/// projects was read as production code. A parallel session left a full untracked copy of the
/// repository at <c>&lt;root&gt;/.git-audit-e/</c> and four tests went red on a clean checkout of
/// <c>main</c> — one reported <c>.git-audit-e</c> as a deployable host, another flagged sites in
/// <c>.git-audit-e/ProcuLink.Infrastructure/Services/OpsHealthService.cs</c>, and the
/// retired-subsystem scans found entities that had been deleted from this repository months
/// earlier but were still alive in that older copy. Nobody wrote the code that failed. This is the
/// same trap the project already knows about for <c>grep</c> — "hits only under
/// <c>.claude/worktrees/</c> mean you grepped the wrong root" — arriving in the test suite, where
/// it produces a red build with no author.</para>
///
/// <para><b>Two rules, because either alone has a hole.</b></para>
/// <list type="number">
///   <item><description><b>Never descend into a directory whose name starts with <c>.</c></b>, at
///   any depth. That is <c>.git-audit-e</c>, <c>.claude/worktrees/</c> (where CLAUDE.md tells every
///   parallel session to work, so this is not hypothetical), <c>.vs</c>, <c>.git</c> and whatever
///   the next tool drops there. It costs nothing real: the only tracked dot-directory in this
///   repository is <c>.github</c>, which holds no C#. This rule holds with or without git.</description></item>
///   <item><description><b>When git is available, the corpus is what git tracks</b>, plus files
///   that are new, not ignored, and sit under a top-level directory git already tracks. A copy
///   named without a leading dot — <c>ProcuLink-backup/</c>, <c>audit-2026-08/</c> — passes every
///   name-shaped filter anyone will ever write, and is refused here because git has never heard of
///   it. A guard reading an untracked copy is asserting about code that will never ship.</description></item>
/// </list>
///
/// <para><b>Why new-but-unstaged files are kept.</b> Tracked-only would be simpler and would make
/// every guard blind to a production file the author had just written — green locally, red the
/// moment CI saw the commit. This project has been bitten by "local green ≠ CI green" often enough
/// that the extra rule is worth its weight: untracked files count when, and only when, their
/// top-level directory is itself tracked.</para>
///
/// <para><b>Exclusions are matched against the path RELATIVE to the repository root, never the
/// absolute path.</b> A worktree root IS <c>&lt;repo&gt;/.claude/worktrees/&lt;name&gt;</c>, so
/// every absolute path beneath it contains <c>/.claude/</c> — matching absolutely excluded the
/// entire repository and left the guards scanning nothing. That was a real, merged bug (PR #132);
/// do not reintroduce it.</para>
/// </summary>
public static class RepoSourceCorpus
{
    /// <summary>A repository source file: its repo-root-relative forward-slashed path, and where to read it.</summary>
    public sealed record RepoFile(string Relative, string FullPath);

    /// <summary>Directory names that never hold shippable source, at any depth.</summary>
    private static readonly string[] NeverDescend = { "bin", "obj", "node_modules" };

    /// <summary>How long git gets before the corpus falls back to walking the filesystem.</summary>
    private const int GitTimeoutMs = 60_000;

    /// <summary>
    /// Walks up from the test assembly's output directory to the folder holding
    /// <c>ProcuLink.slnx</c> — this repo's solution file (there is no <c>.sln</c>). Throws rather
    /// than returning null: a scanner that cannot find the source tree must fail loudly, not
    /// quietly scan nothing. Worktree-safe, because the walk stops at the FIRST
    /// <c>ProcuLink.slnx</c> above the binaries, which is the worktree's own root.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// True when this repo-root-relative path lies outside the tree that can ship: under any
    /// dot-directory, under build output, or under <c>node_modules</c>. Accepts either slash.
    /// </summary>
    public static bool IsOutsideTheShippingTree(string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/');

        // The last segment is the file name; only DIRECTORY segments are judged here, so a
        // dot-file such as .editorconfig is not mistaken for a dot-directory.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0) continue;
            if (segment[0] == '.') return true;
            if (NeverDescend.Contains(segment, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Every C# file in this repository, ordered, repo-root-relative. Narrowing this to production
    /// code — dropping test projects, migrations, a scanner's own source — is each guard's own job
    /// and deliberately not done here; the guards disagree about what "production" means and each
    /// disagreement is documented where it lives.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When the corpus is empty. Empty is the shape every one of these guards passes vacuously on,
    /// so it must be an error rather than a quiet green.
    /// </exception>
    public static IReadOnlyList<RepoFile> CsFiles(string repoRoot)
    {
        var relatives = GitCsFiles(repoRoot) ?? WalkedCsFiles(repoRoot);

        var files = relatives
            .Select(r => new RepoFile(r, Path.Combine(repoRoot, r.Replace('/', Path.DirectorySeparatorChar))))
            // git lists a deleted-but-unstaged file in its index; handing that path to a reader
            // turns every guard into an IO error instead of an architecture verdict.
            .Where(f => File.Exists(f.FullPath))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"RepoSourceCorpus found no C# source under '{repoRoot}'. A guard handed an empty "
                + "corpus reports green having examined nothing, so this is an error rather than a "
                + "pass. Either the root is wrong, or the exclusions now match everything.");
        }

        return files;
    }

    /// <summary>
    /// The top-level directories that belong to this checkout — dot-directories, build output and
    /// (when git is available) anything untracked removed. The list a guard should iterate instead
    /// of <c>Directory.EnumerateDirectories(repoRoot)</c>.
    /// </summary>
    public static IReadOnlyList<string> TopLevelDirectories(string repoRoot)
    {
        var tracked = GitTrackedTopLevelEntries(repoRoot);

        return Directory.EnumerateDirectories(repoRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Where(name => name[0] != '.')
            .Where(name => !NeverDescend.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Where(name => tracked is null || tracked.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when a <c>git</c> executable can be run at all.</summary>
    public static bool GitIsAvailable() => RunGit(Path.GetTempPath(), "--version") is not null;

    /// <summary>
    /// The corpus according to git: tracked C#, plus new not-ignored C# under a top-level directory
    /// git already tracks. Null — not empty — when git cannot answer, so the caller can tell "git
    /// is unavailable" apart from "this repository has no C#".
    /// </summary>
    public static IReadOnlyList<string>? GitCsFiles(string repoRoot)
    {
        var tracked = RunGit(repoRoot, "ls-files -z");
        if (tracked is null) return null;

        var trackedTopLevel = tracked
            .Select(TopLevelOf)
            .ToHashSet(StringComparer.Ordinal);

        var untracked = RunGit(repoRoot, "ls-files -z --others --exclude-standard")
                        ?? Array.Empty<string>();

        return tracked
            .Concat(untracked.Where(p => trackedTopLevel.Contains(TopLevelOf(p))))
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsOutsideTheShippingTree(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The corpus without git: a manual recursive walk that REFUSES TO DESCEND into an excluded
    /// directory rather than enumerating everything and filtering afterwards. That is not only
    /// faster — <c>.claude/worktrees/</c> holds tens of thousands of C# files in a long-running
    /// checkout — it is the difference between "we did not report it" and "we never read it".
    /// </summary>
    public static IReadOnlyList<string> WalkedCsFiles(string repoRoot)
    {
        var found = new List<string>();
        Walk(repoRoot, string.Empty);
        found.Sort(StringComparer.Ordinal);
        return found;

        void Walk(string directory, string prefix)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                found.Add(prefix + Path.GetFileName(file));
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (string.IsNullOrEmpty(name)) continue;
                if (name[0] == '.') continue;
                if (NeverDescend.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                Walk(child, prefix + name + "/");
            }
        }
    }

    /// <summary>
    /// The top-level names git tracks — directories and root files alike. Null when git cannot
    /// answer, which callers read as "apply the name rules only".
    /// </summary>
    private static HashSet<string>? GitTrackedTopLevelEntries(string repoRoot)
    {
        var tracked = RunGit(repoRoot, "ls-files -z");
        return tracked?.Select(TopLevelOf).ToHashSet(StringComparer.Ordinal);
    }

    private static string TopLevelOf(string relative)
    {
        var slash = relative.IndexOf('/');
        return slash < 0 ? relative : relative[..slash];
    }

    /// <summary>
    /// Runs git in <paramref name="workingDirectory"/> and splits its NUL-separated output. Null on
    /// any failure — no git on PATH, not a repository, a non-zero exit, a timeout — so every caller
    /// degrades to the filesystem walk instead of throwing. <c>-z</c> is what makes this safe:
    /// without it git quotes and escapes non-ASCII paths.
    /// </summary>
    private static IReadOnlyList<string>? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            // Read before waiting: a full pipe buffer deadlocks a process that is still writing.
            // Both pipes are drained CONCURRENTLY — draining stdout to the end first would deadlock
            // any git invocation whose stderr outgrew its buffer while stdout was still open.
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            stderr.GetAwaiter().GetResult();

            if (!process.WaitForExit(GitTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            if (process.ExitCode != 0) return null;

            return stdout
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Replace('\\', '/'))
                .ToList();
        }
        catch
        {
            // No git, no permission, a broken PATH — all of them mean "fall back to the walk".
            return null;
        }
    }
}
