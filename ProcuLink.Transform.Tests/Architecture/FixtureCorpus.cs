using System.Diagnostics;
using System.Text;

namespace ProcuLink.Transform.Tests.Architecture;

/// <summary>
/// The single definition of "the fixture corpus", shared by the two guards that police it:
/// <see cref="FixtureDeIdentificationGuardTests"/> (file CONTENTS) and
/// <see cref="FixtureNamingGuardTests"/> (file PATHS).
///
/// It lives here so the two cannot drift apart. A corpus query that quietly stopped matching a
/// directory would otherwise silence one guard while the other kept passing, and the passing one
/// would be read as evidence that the directory is clean.
/// </summary>
internal static class FixtureCorpus
{
    /// <summary>
    /// Tracked files whose <em>directory</em> path contains a segment ending in "Fixtures".
    /// Derived from git rather than typed out — a hand-maintained list is how the next fixture
    /// directory gets added without the guards noticing. Reading from git (not the filesystem)
    /// is also deliberate: real customer documents are kept untracked precisely so they are not
    /// committed, and a filesystem walk would flag them.
    /// </summary>
    internal static IReadOnlyList<string> TrackedFixtureFiles(string repoRoot)
        => TrackedFiles(repoRoot, pathspec: null)
            .Where(IsUnderFixturesDirectory)
            .ToList();

    /// <summary>
    /// Every tracked file matching <paramref name="pathspec"/> (all tracked files when null).
    /// Shared with <see cref="SourcePersonalDataGuardTests"/>, which polices <c>.cs</c> source
    /// rather than the fixture corpus: the two sweeps ask git the same question in the same way,
    /// so a change to how "tracked" is determined cannot silently apply to one and not the other.
    /// </summary>
    internal static IReadOnlyList<string> TrackedFiles(string repoRoot, string? pathspec)
    {
        var args = pathspec is null ? "ls-files -z" : $"ls-files -z -- \"{pathspec}\"";

        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start git to enumerate tracked fixtures");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"`git ls-files` failed in {repoRoot}: {stderr}");
        }

        return stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    internal static bool IsUnderFixturesDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++) // directory segments only
        {
            if (segments[i].EndsWith("Fixtures", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find ProcuLink.slnx walking up from {AppContext.BaseDirectory}");
    }
}
