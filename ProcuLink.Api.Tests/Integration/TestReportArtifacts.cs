using FluentAssertions;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Where a report a test GENERATES is allowed to land.
///
/// <para>Not the working tree. A report written straight into <c>docs/</c> is rewritten with fresh
/// GUIDs and fresh timings on every run, so a full suite run leaves the tree dirty with a diff that
/// carries no information. That trains everyone to skim past <c>git status</c>, and the churn
/// eventually rides along in someone's commit. The committed copies under <c>docs/ops/</c> are
/// snapshots a human updates deliberately; the live output of a run lands here instead.</para>
///
/// <para>The directory is <c>artifacts/</c> at the repo root, which <c>.gitignore</c> already
/// covers, unless <c>PROCULINK_TEST_ARTIFACTS_DIR</c> names somewhere else (CI collecting the
/// reports as build artefacts, say).</para>
/// </summary>
internal static class TestReportArtifacts
{
    internal const string DirectoryOverrideVariable = "PROCULINK_TEST_ARTIFACTS_DIR";

    /// <summary>The directory holding <c>ProcuLink.slnx</c>, or null when the tests run detached from it.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>
    /// The repo root, for tests that execute or compare against a file that SHIPS in the repo.
    /// Fails the test rather than returning null, because such a test cannot run without it.
    /// </summary>
    internal static string RepoRoot()
    {
        var root = FindRepoRoot();
        root.Should().NotBeNull("a test that reads a shipped artefact must be able to locate the repo root");
        return root!;
    }

    internal static string ArtifactsDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var root = FindRepoRoot();
        return root is not null
            ? Path.Combine(root, "artifacts", "test-reports")
            : Path.Combine(AppContext.BaseDirectory, "test-reports");
    }

    /// <summary>Writes a generated report and returns the path it went to, for the test output.</summary>
    internal static async Task<string> WriteAsync(string fileName, string content)
    {
        var directory = ArtifactsDirectory();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
