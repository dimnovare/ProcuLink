using System.Text.RegularExpressions;
using FluentAssertions;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-21 (d) — "set <c>Connections__RevisionAuthority</c> on every service that resolves an
/// effective config" is a CONVENTION, and a convention nothing enforces is not an answer. This is
/// the enforcement.
///
/// <para><b>The failure mode being closed.</b> Today exactly two deployed hosts resolve an
/// effective connection config: the API (<c>ProcuLink</c> on Railway) and the Worker
/// (<c>aware-amazement</c>). Both carry the flag. Add a third host tomorrow — a dedicated ingest
/// service, a replay runner, an ERP bridge — register
/// <c>IEffectiveConnectionConfigResolver</c> in it, and NOTHING would notice that its Railway
/// service has no <c>Connections__RevisionAuthority</c> variable. Every order that host processed
/// would silently read the LIVE mutable tables while every other host honoured the pin: two
/// contradictory answers to "what config governed this order", with no error anywhere.</para>
///
/// <para><b>How it is caught.</b> A SOURCE scan (not reflection — DI registrations live in method
/// bodies, which reflection cannot see) finds every host project that registers the resolver, and
/// asserts that set equals <see cref="RevisionAuthorityHosts.All"/>. The new host cannot merge
/// without being added to that list, and the list's entries are the checklist the deployment
/// runbook is generated from — so adding one forces the author to name its deployed service and
/// its config file, which is exactly the step that would otherwise be forgotten.</para>
///
/// <para>Rule R5 — the comment on <see cref="RevisionAuthorityHosts"/> asserts "these are the
/// hosts that resolve an effective config". These tests are that claim's proof obligation.</para>
/// </summary>
public class RevisionAuthorityHostCoverageTests
{
    /// <summary>
    /// Any mention of the resolver ABSTRACTION or its implementation. Deliberately not a
    /// registration-shaped pattern.
    ///
    /// <para>The first version of this test matched
    /// <c>AddScoped&lt;IEffectiveConnectionConfigResolver,</c> in files named <c>Program.cs</c>. An
    /// adversarial review broke it in one minute: the single-generic factory overload
    /// (<c>AddScoped&lt;IFoo&gt;(sp =&gt; new Foo(...))</c>) has no comma after the type argument
    /// and so did not match — and that idiom is used THREE times in this repo's own hosts today
    /// (<c>ProcuLink.Api/Program.cs:358</c>, <c>:515</c>, <c>ProcuLink.Worker/Program.cs:174</c>).
    /// So did <c>AddSingleton</c>, <c>AddTransient</c>, <c>AddScoped(typeof(..), typeof(..))</c>,
    /// <c>TryAdd*</c>, direct <c>new EffectiveConnectionConfigResolver(...)</c>, and registration
    /// from any file not called <c>Program.cs</c> (a <c>ServiceCollectionExtensions</c>, a shared
    /// <c>AddProcuLinkInfrastructure()</c>). A guard that a competent engineer defeats by writing
    /// idiomatic code is not a guard.</para>
    ///
    /// <para>So the scan is now the opposite shape: ANY production file that names the resolver at
    /// all marks its project. Over-matching is the safe direction — a false positive is one line
    /// added to the roster; a false negative is a deployed host silently reading the live tables.
    /// The defining projects are excluded by <see cref="DefiningProjects"/> because declaring a
    /// type is not resolving a config with it.</para>
    /// </summary>
    private static readonly Regex ResolverMention = new(
        @"\b(I?EffectiveConnectionConfigResolver)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The projects that DEFINE the resolver (interface in Core, implementation in Infrastructure).
    /// They are libraries — they never resolve a config for a deployed process of their own — so
    /// naming the type there is not host behaviour.
    /// </summary>
    private static readonly string[] DefiningProjects = { "ProcuLink.Core", "ProcuLink.Infrastructure" };

    /// <summary>
    /// Every host project that so much as NAMES the effective-config resolver must be declared in
    /// <see cref="RevisionAuthorityHosts.All"/> — and every declared host must really use it, so
    /// the list cannot rot in the other direction either.
    /// </summary>
    [Fact]
    public void EveryHostThatResolvesAnEffectiveConfig_IsDeclaredInTheEnforcedHostList()
    {
        var root = FindRepoRoot();

        var usingProjects = ProductionSourceFiles(root)
            .Where(f => ResolverMention.IsMatch(File.ReadAllText(f.Path)))
            .Select(f => f.Project)
            .Where(p => !DefiningProjects.Contains(p, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        usingProjects.Should().NotBeEmpty(
            "the scan must find the known hosts — an empty result means the scan, not the code, changed");

        var declared = RevisionAuthorityHosts.All
            .Select(h => h.ProjectName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        usingProjects.Should().BeEquivalentTo(declared,
            "every host that resolves an effective connection config MUST be listed in "
            + "RevisionAuthorityHosts.All, because that list is what tells a deployer which Railway "
            + "service needs Connections__RevisionAuthority set. A host missing from the list ships "
            + "reading the live tables while its siblings honour the pin — silently.");
    }

    /// <summary>
    /// Rule R5 — <c>StartupConfigurationValidator.AnnounceRevisionAuthority</c>'s own comment claims
    /// that riding the <c>Validate</c> seam "guarantees a new host cannot gain the announcement by
    /// accident and lose it by omission". That guarantee is worth exactly as much as the claim that
    /// every host calls <c>Validate</c> — which, until this test, nothing asserted. A third host
    /// that skipped it would announce nothing and no other test would notice, so the Worker-shaped
    /// hole (no HTTP, therefore no health endpoint, therefore the log line is the ONLY surface)
    /// would reopen in silence.
    /// </summary>
    [Fact]
    public void EveryDeclaredHost_CallsTheStartupValidator_WhichIsWhatCarriesTheAnnouncement()
    {
        var root = FindRepoRoot();
        var hosts = RevisionAuthorityHosts.All;

        // The sweep below only ever checks the hosts this roster names, so an emptied or shortened
        // roster is a green run that examined no host at all — precisely the Worker-shaped hole
        // this test exists to keep shut, arrived at from the other side. Pinned by NAME rather than
        // by count because a THIRD host is expected to be added here one day and must not break
        // this test; losing one of the two known hosts is the failure worth catching.
        hosts.Select(h => h.ProjectName).Should().Contain(
            new[] { "ProcuLink.Api", "ProcuLink.Worker" },
            "the API and the Worker are the two hosts that resolve an effective connection config "
            + "today, and each is checked here only because this roster names it — a roster that "
            + "lost one would let that host ship with no startup announcement at all, and this test "
            + "would still report green having never looked at it");

        foreach (var host in hosts)
        {
            var sources = ProductionSourceFiles(root)
                .Where(f => string.Equals(f.Project, host.ProjectName, StringComparison.Ordinal))
                .ToList();

            sources.Should().NotBeEmpty($"{host.ProjectName} must have production sources to scan");

            sources.Any(f => File.ReadAllText(f.Path).Contains("StartupConfigurationValidator.Validate"))
                .Should().BeTrue(
                    $"{host.ProjectName} must call StartupConfigurationValidator.Validate — that call is "
                    + "what emits the effective revision-authority value at startup, and for a host that "
                    + "serves no HTTP it is the only surface on which the value can ever be observed");
        }
    }

    /// <summary>
    /// The scan's own worktree-safety, proven without needing a real worktree: a synthetic tree
    /// whose ROOT sits under <c>.claude/worktrees/</c>, which is exactly where
    /// <c>&lt;repo&gt;/.claude/worktrees/&lt;name&gt;</c> puts a checkout. With the exclusions
    /// matched against the ABSOLUTE path this found zero files, and the two source-scanning tests
    /// above failed on a clean <c>origin/main</c> — the guard was unusable in the workspace
    /// CLAUDE.md tells every parallel session to work in, which is how a real guard gets deleted
    /// rather than fixed.
    ///
    /// <para>It equally pins what the fix must NOT have relaxed. Widening the corpus is not the
    /// safe direction here: build output would let a stale generated file answer for a host, a test
    /// naming the resolver would enrol <c>ProcuLink.Api.Tests</c> as a deployed host, and a NESTED
    /// worktree would let another session's in-progress code decide whether this branch's host list
    /// is complete. So the expected set is asserted exactly, not merely as non-empty.</para>
    /// </summary>
    [Fact]
    public void TheScan_FindsSourcesWhenTheCheckoutItselfSitsUnderDotClaudeWorktrees()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"plk-revauth-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, ".claude", "worktrees", "sibling-session");

        try
        {
            WriteSyntheticSource(root, "ProcuLink.Api/Program.cs");
            WriteSyntheticSource(root, "ProcuLink.Worker/Program.cs");
            WriteSyntheticSource(root, "ProcuLink.Api/bin/Debug/net8.0/Generated.cs");
            WriteSyntheticSource(root, "ProcuLink.Api/obj/Debug/net8.0/Generated.cs");
            WriteSyntheticSource(root, "ProcuLink.Api.Tests/SomethingTests.cs");
            WriteSyntheticSource(root, ".claude/worktrees/other-session/ProcuLink.Api/Program.cs");

            var found = ProductionSourceFiles(root)
                .Select(f => RelativeTo(root, f.Path))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            found.Should().Equal(
                new[] { "ProcuLink.Api/Program.cs", "ProcuLink.Worker/Program.cs" },
                "a checkout that itself sits under .claude/worktrees must still see its OWN "
                + "production sources — and must still see nothing from build output, from the test "
                + "projects, or from a sibling session's nested worktree");
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    private static void WriteSyntheticSource(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "// synthetic source — content is irrelevant, only the path is\n");
    }

    private sealed record SourceFile(string Project, string Path);

    /// <summary>
    /// Every checked-in production .cs file, tagged with its owning project directory. Excludes
    /// build output, other sessions' worktrees under <c>.claude/</c>, and the test projects — a
    /// test naming the resolver is not a host resolving a config with it, which is precisely how a
    /// real host would hide from a naive scan.
    ///
    /// <para><b>Exclusions match the path RELATIVE to the repo root, never the absolute path.</b>
    /// They used to match absolutely, and CLAUDE.md's own instruction to do parallel work in
    /// isolated worktrees puts the checkout at
    /// <c>&lt;repo&gt;/.claude/worktrees/&lt;name&gt;</c> — where every absolute path under the root
    /// contains <c>/.claude/</c>, so the <c>.claude</c> rule excluded the ENTIRE repository and the
    /// scan found nothing. Both callers treat an empty result as a failure (correctly), so the
    /// guard did not go false-green; it went unusable in the workspace the project documents. The
    /// two sibling source scans already match relatively —
    /// <see cref="RetiredSubsystemsStayRetiredTests"/> and <c>OrphanDetector.IsExcluded</c>, which
    /// <see cref="AcceptanceGateSingleDoorTests"/> goes through — and
    /// <c>VacuousTestPassScanner.ExcludedSegments</c> documents this exact trap. Pinned by
    /// <see cref="TheScan_FindsSourcesWhenTheCheckoutItselfSitsUnderDotClaudeWorktrees"/>.</para>
    /// </summary>
    private static IEnumerable<SourceFile> ProductionSourceFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: RelativeTo(root, path)))
            .Where(f => !IsExcluded(f.Relative))
            .Select(f => new SourceFile(ProjectOf(f.Relative), f.Path))
            .Where(f => f.Project.Length > 0 && !f.Project.EndsWith("Tests", StringComparison.Ordinal));

    /// <summary>Repo-root-relative and forward-slashed — the one form every rule below matches.</summary>
    private static string RelativeTo(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Build output, and the full repo copies parallel sessions keep under <c>.claude/worktrees/</c>
    /// — reading those would mix another session's in-progress code into the answer. The nested
    /// form is kept as well as the leading one so a worktree inside a worktree stays excluded.
    /// </summary>
    private static bool IsExcluded(string relative) =>
        relative.StartsWith("obj/", StringComparison.Ordinal)
        || relative.StartsWith("bin/", StringComparison.Ordinal)
        || relative.StartsWith(".claude/", StringComparison.Ordinal)
        || relative.Contains("/obj/", StringComparison.Ordinal)
        || relative.Contains("/bin/", StringComparison.Ordinal)
        || relative.Contains("/.claude/", StringComparison.Ordinal);

    /// <summary>The top-level project directory a file belongs to, relative to the repo root.</summary>
    private static string ProjectOf(string relative)
    {
        var first = relative.Split('/')[0];
        return first.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "" : first;
    }

    /// <summary>
    /// Each declared host must name the deployed service that carries the variable, and the
    /// appsettings file that carries the local default. Blank strings would satisfy the list check
    /// above while telling a deployer nothing.
    /// </summary>
    [Fact]
    public void EveryDeclaredHost_NamesItsDeployedServiceAndItsConfigFile()
    {
        var root = FindRepoRoot();

        RevisionAuthorityHosts.All.Should().NotBeEmpty();

        foreach (var host in RevisionAuthorityHosts.All)
        {
            host.ProjectName.Should().NotBeNullOrWhiteSpace();
            host.DeployedServiceName.Should().NotBeNullOrWhiteSpace(
                $"{host.ProjectName} must name the Railway service whose variables a deployer has to set");

            var configPath = Path.Combine(root, host.ProjectName, host.DevelopmentConfigFile);
            File.Exists(configPath).Should().BeTrue(
                $"{host.ProjectName} declares its development config as {host.DevelopmentConfigFile}, "
                + $"but {configPath} does not exist");
        }
    }

    /// <summary>
    /// The production smoke runbook must exist and must name every declared host's deployed
    /// service. A runbook that silently omits the new host is how the third service ships without
    /// the variable — the same class of gap the list itself closes.
    /// </summary>
    [Fact]
    public void TheProductionRunbook_ExistsAndNamesEveryDeclaredHost()
    {
        var root = FindRepoRoot();
        var runbook = Path.Combine(root, "docs", "ops", "revision-authority-production-smoke.md");

        File.Exists(runbook).Should().BeTrue(
            $"the WP-21 production smoke runbook must be checked in at {runbook} — the live "
            + "observation is a founder action and it needs written, undoable steps");

        var text = File.ReadAllText(runbook);

        text.Should().Contain(RevisionAuthorityHosts.EnvironmentVariableName,
            "the runbook must name the exact environment variable a deployer sets");

        foreach (var host in RevisionAuthorityHosts.All)
        {
            text.Should().Contain(host.DeployedServiceName,
                $"the runbook must tell the operator to check the '{host.DeployedServiceName}' service");
        }
    }

    private static string FindRepoRoot()
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
}
