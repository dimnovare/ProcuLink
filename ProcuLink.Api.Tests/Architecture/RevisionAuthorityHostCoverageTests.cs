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
    /// <summary>The DI registration that makes a host an effective-config resolver.</summary>
    private static readonly Regex Registration = new(
        @"AddScoped\s*<\s*IEffectiveConnectionConfigResolver\s*,",
        RegexOptions.Compiled);

    /// <summary>
    /// Every host project whose <c>Program.cs</c> registers the resolver must be declared in
    /// <see cref="RevisionAuthorityHosts.All"/> — and every declared host must really register it,
    /// so the list cannot rot in the other direction either.
    /// </summary>
    [Fact]
    public void EveryHostThatResolvesAnEffectiveConfig_IsDeclaredInTheEnforcedHostList()
    {
        var root = FindRepoRoot();

        var registeringProjects = Directory
            .EnumerateFiles(root, "Program.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"))
            .Where(p => Registration.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetFileName(Path.GetDirectoryName(p))!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        registeringProjects.Should().NotBeEmpty(
            "the scan must find the known hosts — an empty result means the regex, not the code, changed");

        var declared = RevisionAuthorityHosts.All
            .Select(h => h.ProjectName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        registeringProjects.Should().BeEquivalentTo(declared,
            "every host that resolves an effective connection config MUST be listed in "
            + "RevisionAuthorityHosts.All, because that list is what tells a deployer which Railway "
            + "service needs Connections__RevisionAuthority set. A host missing from the list ships "
            + "reading the live tables while its siblings honour the pin — silently.");
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
