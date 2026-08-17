using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// A workflow that makes real outbound calls must not be reachable from a pull request.
///
/// <para>WHY THIS GUARD EXISTS. <c>.github/workflows/live-delivery.yml</c> fires the real
/// <c>HttpDeliveryDispatcher</c> at a disposable endpoint, and <c>postmark-ip-drift.yml</c> already
/// splits its network job away from the PR path for the same reason. Both explain the restriction in
/// a comment. A comment is not a check, and this repository has repeatedly found that a sentence
/// claiming enforcement is precisely where people stop looking — see the "doc comments assert
/// enforcement elsewhere" pattern that produced a P0.</para>
///
/// <para>Two things go wrong without this test, in order of how bad they are. The mild one: a third
/// party's bad minute reddens an unrelated pull request, people learn to ignore the red, and the
/// check stops meaning anything. The severe one: someone later points a network workflow at a real
/// endpoint and adds a credential to reach it. From that moment a <c>pull_request</c> trigger lets a
/// fork PR run repository workflow code with that credential in scope, and
/// <c>pull_request_target</c> runs with the base repository's secrets by design. The restriction is
/// free to establish now and expensive to establish after the leak.</para>
///
/// <para>WHY IT SCANS RATHER THAN NAMING A FILE. Naming <c>live-delivery.yml</c> would pass forever
/// once the pattern was copied into a second workflow — the shape in which guards here habitually go
/// blind. Discovery is by property: any workflow that opts into the live-endpoint switch or starts
/// the disposable sink. Whatever that finds is what gets asserted, so a second network workflow is
/// covered without editing this file.</para>
/// </summary>
public class NetworkWorkflowTriggerTests
{
    /// <summary>
    /// What marks a workflow as making real outbound calls. Either signal suffices; the pair exists
    /// so renaming one does not silently empty the scan.
    /// </summary>
    private static readonly string[] NetworkSignals =
    {
        "PROCULINK_LIVE_ENDPOINT_TESTS",
        "tools/live-delivery-sink/",
    };

    /// <summary>
    /// The only triggers such a workflow may declare. Both require either write access or GitHub's
    /// own scheduler, and both can only run workflow code that already exists on the ref they target.
    /// </summary>
    private static readonly string[] AllowedTriggers = { "workflow_dispatch", "schedule" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull(
            "the tests must run from inside the ProcuLink checkout (ProcuLink.slnx not found above the test binaries)");
        return dir!.FullName;
    }

    private static List<(string File, string Source)> NetworkWorkflows()
    {
        var dir = Path.Combine(RepoRoot(), ".github", "workflows");
        if (!Directory.Exists(dir)) return new List<(string, string)>();

        return Directory.EnumerateFiles(dir)
            .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(p => (File: Path.GetFileName(p), Source: System.IO.File.ReadAllText(p)))
            .Where(w => NetworkSignals.Any(s => w.Source.Contains(s, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// The trigger names in the <c>on:</c> block.
    ///
    /// <para>Comment lines are stripped FIRST. The workflow this guard exists for explains, in prose,
    /// why it has no <c>pull_request</c> trigger — a guard that failed on its own justification would
    /// be worse than no guard, because the obvious fix would be to delete the explanation.</para>
    /// </summary>
    private static List<string> DeclaredTriggers(string source)
    {
        var lines = source.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !Regex.IsMatch(l, @"^\s*#"))
            .ToList();

        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^on:\s*$"));
        if (start == -1)
        {
            // Inline forms (`on: [push]`, `on: workflow_dispatch`). Returned raw so the assertion
            // below fails loudly rather than reporting "no triggers", which would pass.
            var inline = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^on:\s*\S"));
            return inline is null
                ? new List<string>()
                : new List<string> { Regex.Replace(inline, @"^on:\s*", string.Empty).Trim() };
        }

        var triggers = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (Regex.IsMatch(line, @"^\S")) break; // next column-0 key ends the block
            var match = Regex.Match(line, @"^ {2}([A-Za-z_][A-Za-z0-9_]*):");
            if (match.Success) triggers.Add(match.Groups[1].Value);
        }
        return triggers;
    }

    [Fact]
    public void At_least_one_network_workflow_is_found_otherwise_this_class_asserts_nothing()
    {
        // The scan is by content, so a rename or a moved script would make it match nothing — and a
        // guard that inspects nothing reports exactly the same green as one that inspects everything.
        NetworkWorkflows().Should().NotBeEmpty(
            $"no workflow under .github/workflows mentions any of {string.Join(", ", NetworkSignals)}. " +
            "Either the live-delivery workflow was deleted, or it now opts in under a name this scan " +
            "does not know — add that name to NetworkSignals.");
    }

    [Fact]
    public void Network_workflows_are_triggered_only_by_workflow_dispatch_or_schedule()
    {
        foreach (var (file, source) in NetworkWorkflows())
        {
            var triggers = DeclaredTriggers(source);

            triggers.Should().NotBeEmpty($"{file} declares no triggers — its on: block could not be parsed.");

            var forbidden = triggers.Where(t => !AllowedTriggers.Contains(t)).ToList();
            forbidden.Should().BeEmpty(
                $"{file} makes real outbound calls and declares the trigger(s) [{string.Join(", ", forbidden)}]. " +
                $"Only {string.Join(" / ", AllowedTriggers)} are permitted: anything else can be reached by " +
                "something other than a maintainer pressing a button — a fork PR under pull_request_target " +
                "runs base-repository workflow code, and a push runs on any branch. To run it on a branch, " +
                $"dispatch it: gh workflow run {file} --ref <branch>.");
        }
    }

    [Fact]
    public void The_disposable_sink_is_stopped_even_when_the_run_fails()
    {
        foreach (var (file, source) in NetworkWorkflows())
        {
            if (!source.Contains("live-delivery-sink/sink.mjs", StringComparison.Ordinal)) continue;

            var lines = source.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !Regex.IsMatch(l, @"^\s*#"))
                .ToList();

            var stopIndex = lines.FindIndex(l => l.Contains("kill \"$SINK_PID\"", StringComparison.Ordinal));
            stopIndex.Should().BeGreaterThan(-1,
                $"{file} starts the disposable sink but never stops it, so a listener outlives the step " +
                "that created it.");

            // Look back over the step that owns the kill for `if: always()`. The `always()` is the
            // easy half to lose in a refactor, and the runs most likely to strand a listener are the
            // failing ones — exactly the runs a plain step would skip.
            var window = string.Join("\n", lines.Skip(Math.Max(0, stopIndex - 10)).Take(Math.Min(11, stopIndex + 1)));
            Regex.IsMatch(window, @"if:\s*always\(\)").Should().BeTrue(
                $"{file} stops the disposable sink without `if: always()`. A failed assertion, a timeout " +
                "or a cancellation would then skip the teardown.");
        }
    }
}
